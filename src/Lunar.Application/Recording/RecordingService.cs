using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Reactive;
using Lunar.Application.Settings;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;

namespace Lunar.Application.Recording;

/// <summary>
/// The one recorder (ADR-0022): the Records view and the island both drive this instance, and only one recording exists at a time. Calls are
/// serialized, so a double press is simply a second request that finds the work done. Whatever ends a recording, what was captured is saved.
/// <see cref="Changes"/> follows ADR-0006. Log events carry fixed names and codes only: no path, device name or audio.
/// </summary>
public sealed class RecordingService : IDisposable
{
    internal static readonly TimeSpan LimitCheckInterval = TimeSpan.FromSeconds(5);
    private const string Component = "Recording";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _pendingLock = new();
    private readonly StateBroadcaster<RecordingSnapshot> _changes = new();
    private readonly IVoiceCaptureEngine _engine;
    private readonly IAudioInputCatalog _catalog;
    private readonly IRecordingFiles _files;
    private readonly SettingsService _settings;
    private readonly IClock _clock;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private RecordingSnapshot _current = RecordingSnapshot.Initial;
    private IVoiceCaptureSession? _session;
    private ITimer? _limitTimer;
    private Task _pendingSave = Task.CompletedTask;
    private string? _partPath;
    private string? _baseName;
    private string? _destination;
    private bool _disposed;

    public RecordingService(
        IVoiceCaptureEngine engine,
        IAudioInputCatalog catalog,
        IRecordingFiles files,
        SettingsService settings,
        IClock clock,
        IDiagnosticLog? log = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(clock);
        _engine = engine;
        _catalog = catalog;
        _files = files;
        _settings = settings;
        _clock = clock;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    public RecordingSnapshot Current => Volatile.Read(ref _current);

    public IObservable<RecordingSnapshot> Changes => _changes;

    /// <summary>Time written to the file so far; zero when nothing is being recorded. Safe from any thread.</summary>
    public TimeSpan Recorded => Volatile.Read(ref _session)?.Recorded ?? TimeSpan.Zero;

    /// <summary>Forwarded from the microphone while recording, and one flat reading at the end. Raised on an audio thread.</summary>
    public event Action<VoiceLevels>? LevelsChanged;

    /// <summary>The folder recordings go to.</summary>
    public string Folder => _settings.Current.Recorder.OutputDirectory ?? _files.DefaultFolder;

    /// <summary>The saved microphone choice; null is Automatic (the Windows default).</summary>
    public string? ChosenInputId => _settings.Current.Recorder.InputDeviceId;

    public Task<Result<IReadOnlyList<AudioInput>>> GetInputsAsync(CancellationToken ct) => _catalog.GetInputsAsync(ct);

    /// <summary>Saves the microphone choice for the next recording; null is Automatic. A running recording keeps its microphone.</summary>
    public Task<Result> ChooseInputAsync(string? inputId, CancellationToken ct) =>
        _settings.UpdateAsync(s => s with { Recorder = s.Recorder with { InputDeviceId = inputId } }, ct);

    /// <summary>Saves the folder for the next recording, after checking it can be used.</summary>
    public async Task<Result> ChooseFolderAsync(string folder, CancellationToken ct)
    {
        var valid = _files.ValidateFolder(folder);
        if (valid.IsFailure)
            return valid;
        return await _settings.UpdateAsync(s => s with { Recorder = s.Recorder with { OutputDirectory = folder } }, ct).ConfigureAwait(false);
    }

    /// <summary>Repairs what an unexpected stop left behind; the snapshot tells the user. Call once at start-up. Opens no microphone.</summary>
    public async Task<Result> RecoverAsync(CancellationToken ct)
    {
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            var found = await _files.RecoverAsync(ct).ConfigureAwait(false);
            if (found.IsFailure)
            {
                Log(DiagnosticLevel.Warning, "recording.recover.failed", "Recover", found.Error!.Code);
                return Result.Failure(found.Error!);
            }

            if (found.Value is not null)
            {
                Log(DiagnosticLevel.Information, "recording.recover.found", "Recover");
                Publish(Current with { Notice = RecordingNotice.RecordingRecovered, Recovered = found.Value });
            }

            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Starts recording. Asking while one is under way does nothing.</summary>
    public async Task<Result> StartAsync(CancellationToken ct)
    {
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            if (RecordingRules.IsActive(Current.Phase) || _disposed)
                return Result.Success();

            Publish(new RecordingSnapshot(RecordingPhase.Starting, LastSaved: Current.LastSaved));
            var folder = Folder;
            var valid = _files.ValidateFolder(folder);
            if (valid.IsFailure)
                return FailStart(valid.Error!);
            var free = _files.GetFreeBytes(folder);
            if (free.IsSuccess && free.Value < RecordingRules.MinFreeBytes)
                return FailStart(new OperationError(ErrorCode.IoFailure, "There is not enough free space in that folder to record."));

            var (inputId, notice) = await ResolveInputAsync(ct).ConfigureAwait(false);
            var baseName = RecordingRules.BaseNameFor(_clock.LocalNow.DateTime);
            var part = await _files.ReservePartAsync(folder, baseName, ct).ConfigureAwait(false);
            if (part.IsFailure)
                return FailStart(part.Error!);

            var started = await _engine.StartAsync(inputId, part.Value, ct).ConfigureAwait(false);
            if (started.IsFailure)
            {
                await _files.AbandonAsync(part.Value, CancellationToken.None).ConfigureAwait(false);
                return FailStart(started.Error!);
            }

            _partPath = part.Value;
            _baseName = baseName;
            _destination = folder;
            var session = started.Value;
            session.LevelsChanged += OnLevels;
            session.Failed += OnSessionFailed;
            Volatile.Write(ref _session, session);
            _limitTimer = _time.CreateTimer(_ => CheckLimits(), null, LimitCheckInterval, LimitCheckInterval);
            Log(DiagnosticLevel.Information, "recording.started", "Start");
            Publish(new RecordingSnapshot(RecordingPhase.Recording, notice, LastSaved: Current.LastSaved));
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<Result> PauseAsync(CancellationToken ct) =>
        ChangeAsync(RecordingPhase.Recording, RecordingPhase.Paused, (s, c) => s.PauseAsync(c), ct);

    public Task<Result> ResumeAsync(CancellationToken ct) =>
        ChangeAsync(RecordingPhase.Paused, RecordingPhase.Recording, (s, c) => s.ResumeAsync(c), ct);

    /// <summary>Stops and saves. Asking when nothing is being recorded does nothing. Not cancellable: audio is never given up halfway.</summary>
    public async Task<Result> StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            return await SaveCoreAsync(RecordingNotice.None).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Lunar is closing: a running recording is saved. Bounded by the engine's and the file store's own limits.</summary>
    public async Task ShutdownAsync()
    {
        // A save already on its way goes first, so what it has to tell the user (the microphone was lost, the disk is full) is not lost.
        Task pending;
        lock (_pendingLock)
            pending = _pendingSave;
        await pending.ConfigureAwait(false);
        await StopAsync().ConfigureAwait(false);
    }

    /// <summary>Ends the service. A running recording must have been saved with <see cref="ShutdownAsync"/> first; this only lets go.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _limitTimer?.Dispose();
        _changes.Complete();
    }

    private async Task<bool> EnterAsync(CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<(string? Id, RecordingNotice Notice)> ResolveInputAsync(CancellationToken ct)
    {
        var chosen = ChosenInputId;
        if (chosen is null)
            return (null, RecordingNotice.None);

        // When the list cannot be read the choice is tried as it is: the engine says plainly if it is really gone.
        var inputs = await _catalog.GetInputsAsync(ct).ConfigureAwait(false);
        return inputs.IsSuccess && inputs.Value.All(i => i.Id != chosen)
            ? (null, RecordingNotice.ChosenInputMissing)
            : (chosen, RecordingNotice.None);
    }

    private Result FailStart(OperationError error)
    {
        Log(DiagnosticLevel.Warning, "recording.start.failed", "Start", error.Code);
        if (error.Code == ErrorCode.Cancelled)
        {
            Publish(new RecordingSnapshot(RecordingPhase.Idle, LastSaved: Current.LastSaved));
            return Result.Cancelled();
        }

        Publish(new RecordingSnapshot(RecordingPhase.Failed, Error: error, LastSaved: Current.LastSaved));
        return Result.Failure(error);
    }

    private async Task<Result> ChangeAsync(RecordingPhase from, RecordingPhase to, Func<IVoiceCaptureSession, CancellationToken, Task<Result>> action, CancellationToken ct)
    {
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            if (Current.Phase != from || _session is null)
                return Result.Success();
            var result = await action(_session, ct).ConfigureAwait(false);
            if (result.IsSuccess)
                Publish(Current with { Phase = to, Error = null });
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Under the gate. Whatever went wrong with completing the file, what is on disk still gets the chance to be published: a part file holds
    // playable audio even without its ending, and CommitAsync publishes only real MP3. Otherwise the file stays for the next start's recovery.
    private async Task<Result> SaveCoreAsync(RecordingNotice notice)
    {
        var session = _session;
        if (session is null)
            return Result.Success();

        Publish(Current with { Phase = RecordingPhase.Saving, Error = null });
        _limitTimer?.Dispose();
        _limitTimer = null;
        session.Failed -= OnSessionFailed;
        var finished = await session.FinishAsync(CancellationToken.None).ConfigureAwait(false);
        session.LevelsChanged -= OnLevels;
        Volatile.Write(ref _session, null);
        await session.DisposeAsync().ConfigureAwait(false);
        LevelsChanged?.Invoke(new VoiceLevels(new float[VoiceLevelBands.Count]));
        if (finished.IsFailure)
            Log(DiagnosticLevel.Warning, "recording.finish.failed", "Save", finished.Error!.Code);

        var committed = await _files.CommitAsync(_partPath!, _baseName!, CancellationToken.None).ConfigureAwait(false);
        if (committed.IsFailure)
        {
            Log(DiagnosticLevel.Error, "recording.commit.failed", "Save", committed.Error!.Code);
            Publish(new RecordingSnapshot(RecordingPhase.Failed, notice, committed.Error, Current.LastSaved));
            return Result.Failure(committed.Error!);
        }

        Log(DiagnosticLevel.Information, "recording.saved", "Save");
        Publish(new RecordingSnapshot(RecordingPhase.Idle, notice, LastSaved: committed.Value));
        return Result.Success();
    }

    private void OnLevels(VoiceLevels levels)
    {
        var handlers = LevelsChanged;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<VoiceLevels>>())
        {
            try
            {
                handler(levels);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Isolated: a bad listener must not reach the audio thread.
            }
        }
    }

    private void OnSessionFailed(OperationError error)
    {
        Log(DiagnosticLevel.Warning, "recording.device.lost", "Capture", error.Code);
        SaveInBackground(RecordingNotice.DeviceLost);
    }

    private void CheckLimits()
    {
        var folder = Volatile.Read(ref _destination);
        if (Volatile.Read(ref _session) is not { } session || folder is null)
            return;
        if (session.Recorded >= RecordingRules.MaxDuration)
        {
            SaveInBackground(RecordingNotice.LengthLimitReached);
            return;
        }

        var free = _files.GetFreeBytes(folder);
        if (free.IsSuccess && free.Value < RecordingRules.MinFreeBytes)
            SaveInBackground(RecordingNotice.DiskNearlyFull);
    }

    // The task is kept so that closing Lunar waits for it.
    private void SaveInBackground(RecordingNotice notice)
    {
        lock (_pendingLock)
            _pendingSave = _pendingSave.ContinueWith(_ => SaveFromBackgroundAsync(notice), TaskScheduler.Default).Unwrap();
    }

    private async Task SaveFromBackgroundAsync(RecordingNotice notice)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await SaveCoreAsync(notice).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(DiagnosticLevel.Error, "recording.save.crashed", "Save", ErrorCode.Unexpected, ex.GetType());
            Publish(new RecordingSnapshot(RecordingPhase.Failed, notice, new OperationError(ErrorCode.Unexpected, "The recording could not be saved."), Current.LastSaved));
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(RecordingSnapshot snapshot)
    {
        Volatile.Write(ref _current, snapshot);
        _changes.Publish(snapshot);
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? error = null, Type? exception = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, error, ExceptionType: exception));
}
