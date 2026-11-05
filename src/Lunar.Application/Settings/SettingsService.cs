using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Settings;

/// <summary>What changed: the settings before and after one accepted update.</summary>
public sealed class SettingsChangedEventArgs(LunarSettings previous, LunarSettings current) : EventArgs
{
    public LunarSettings Previous { get; } = previous;

    public LunarSettings Current { get; } = current;
}

/// <summary>
/// The one writer of the user's settings (ADR-0016). Every change is applied to the latest copy, normalized, saved, and only
/// then announced, so two screens can never undo each other's change and nobody sees a value that is not on disk. A failed
/// save changes nothing. Calls are serialized; <see cref="Changed"/> is raised on the thread that finished the save, in the order
/// the changes were made, and a handler must not wait for another update (it should hand the work to its own thread).
/// </summary>
public sealed class SettingsService : IDisposable
{
    private readonly ISettingsRepository _repository;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile LunarSettings _current = LunarSettings.Defaults;
    private volatile SettingsRecovery _recovery = SettingsRecovery.CreatedDefaults;
    private volatile bool _loaded;

    public SettingsService(ISettingsRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        _repository = repository;
    }

    /// <summary>Raised after a change has been saved.</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>The latest saved settings; the defaults until the first successful <see cref="LoadAsync"/>.</summary>
    public LunarSettings Current => _current;

    /// <summary>How the settings were obtained at the first load (a notice for the user unless it is None or CreatedDefaults).</summary>
    public SettingsRecovery Recovery => _recovery;

    /// <summary>True when the file comes from a newer Lunar: it is left untouched and every save is refused.</summary>
    public bool IsReadOnly => _recovery == SettingsRecovery.NewerVersionIgnored;

    /// <summary>
    /// Reads the file once; later calls return what was read. Fails only when the file cannot be read at all (the defaults stay
    /// in <see cref="Current"/> and a later call tries again) or on cancellation.
    /// </summary>
    public async Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
            return Result.Cancelled<SettingsLoadResult>();
        try
        {
            return await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Applies <paramref name="change"/> to the latest settings and saves the repaired result.</summary>
    public async Task<Result> UpdateAsync(Func<LunarSettings, LunarSettings> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (!await EnterAsync(cancellationToken).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            var loaded = await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            if (loaded.IsFailure)
                return Result.Failure(loaded.Error!);
            return await SaveAsync(change(_current), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Returns every preference to its default.</summary>
    public Task<Result> ResetAsync(CancellationToken cancellationToken) => UpdateAsync(_ => LunarSettings.Defaults, cancellationToken);

    public void Dispose() => _gate.Dispose();

    private async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<Result<SettingsLoadResult>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return Result.Success(new SettingsLoadResult(_current, _recovery));

        var loaded = await _repository.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (loaded.IsFailure)
            return loaded;

        _current = loaded.Value.Settings;
        _recovery = loaded.Value.Recovery;
        _loaded = true;
        return loaded;
    }

    private async Task<Result> SaveAsync(LunarSettings requested, CancellationToken cancellationToken)
    {
        var next = SettingsRules.Normalize(requested ?? LunarSettings.Defaults, out _);
        var previous = _current;
        if (next == previous)
            return Result.Success(); // nothing to write, nobody to tell

        var saved = await _repository.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        if (saved.IsFailure)
            return saved;

        _current = next;
        Changed?.Invoke(this, new SettingsChangedEventArgs(previous, next));
        return Result.Success();
    }
}
