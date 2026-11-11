using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Updates;
using Lunar.Application.Reactive;
using Lunar.Application.Settings;
using Lunar.Domain.Shared;
using Lunar.Domain.Updates;

namespace Lunar.Application.Updates;

/// <summary>
/// The update check (ADR-0025). After <see cref="Start"/> it asks the feed shortly after start and then every 24 hours, unless the user turned the
/// check off, in which case nothing is requested at all. A newer release becomes <see cref="UpdatePhase.Available"/>; nothing is downloaded or
/// installed until <see cref="InstallAsync"/>, which only hands a checked installer to Windows Installer. Calls are serialized;
/// <see cref="Changes"/> follows ADR-0006. Holding the prompt back during a focus session or a recording is the presentation's job.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string Component = "Updates";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StateBroadcaster<UpdateSnapshot> _changes = new();
    private readonly IUpdateFeed _feed;
    private readonly IUpdateInstaller _installer;
    private readonly SettingsService _settings;
    private readonly AppVersion _running;
    private readonly IDiagnosticLog? _log;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private UpdateSnapshot _current = UpdateSnapshot.Initial;
    private ITimer? _timer;
    private Task _pendingCheck = Task.CompletedTask;
    private bool _disposed;

    public UpdateService(IUpdateFeed feed, IUpdateInstaller installer, SettingsService settings, AppVersion running, IDiagnosticLog? log = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(installer);
        ArgumentNullException.ThrowIfNull(settings);
        _feed = feed;
        _installer = installer;
        _settings = settings;
        _running = running;
        _log = log;
        _time = time ?? TimeProvider.System;
        _settings.Changed += OnSettingsChanged;
    }

    public UpdateSnapshot Current => Volatile.Read(ref _current);

    public IObservable<UpdateSnapshot> Changes => _changes;

    /// <summary>The version this Lunar is.</summary>
    public AppVersion RunningVersion => _running;

    /// <summary>The check is on (the default).</summary>
    public bool IsEnabled => _settings.Current.Updates.Check;

    /// <summary>The check the schedule started last (completed when none runs).</summary>
    public Task PendingCheck => Volatile.Read(ref _pendingCheck);

    /// <summary>Removes an installer left from an earlier update and starts the schedule. Call once after start-up.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_timer is not null)
            return;
        _installer.RemoveLeftovers();
        _timer = _time.CreateTimer(_ => OnTimer(), null, UpdateRules.FirstCheckDelay, UpdateRules.CheckInterval);
    }

    /// <summary>
    /// Asks the feed now. Does nothing while the check is off or an install is under way. A failure is logged as a fixed event and changes nothing
    /// the user sees; the next check tries again.
    /// </summary>
    public async Task<Result> CheckAsync(CancellationToken ct)
    {
        if (!IsEnabled)
            return Result.Success();
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            if (!IsEnabled || Current.Phase is UpdatePhase.Downloading or UpdatePhase.Installing)
                return Result.Success();

            var latest = await _feed.GetLatestAsync(ct).ConfigureAwait(false);
            if (latest.IsCancelled)
                return Result.Cancelled();
            if (latest.IsFailure)
            {
                Log(DiagnosticLevel.Warning, "update.check.failed", "Check", latest.Error!.Code);
                return latest.Error!.Retryable ? Result.Success() : Result.Failure(latest.Error);
            }

            if (!IsEnabled)
                return Result.Success();
            var release = latest.Value;
            if (release is null || release.Version <= _running)
            {
                Publish(UpdateSnapshot.Initial);
                return Result.Success();
            }

            // Found again by a later check: Later is over, so it is offered again (ADR-0025).
            Log(DiagnosticLevel.Information, "update.available", "Check");
            Publish(new UpdateSnapshot(UpdatePhase.Available, release, 0, null, Postponed: false));
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Later: the prompt goes until the next check finds the release again.</summary>
    public void Postpone()
    {
        var current = Current;
        if (current.Phase is UpdatePhase.Available or UpdatePhase.Failed && current.Release is not null)
            Publish(new UpdateSnapshot(UpdatePhase.Available, current.Release, 0, null, Postponed: true));
    }

    /// <summary>
    /// Install now: downloads and checks the installer, then starts Windows Installer. On success Lunar must exit at once. A cancellation goes
    /// back to <see cref="UpdatePhase.Available"/>; a failure is <see cref="UpdatePhase.Failed"/> with a plain reason and nothing was run.
    /// </summary>
    public async Task<Result> InstallAsync(CancellationToken ct)
    {
        if (!await EnterAsync(ct).ConfigureAwait(false))
            return Result.Cancelled();
        try
        {
            var release = Current.Release;
            if (release is null || Current.Phase is not (UpdatePhase.Available or UpdatePhase.Failed))
                return Result.Failure(new OperationError(ErrorCode.InvalidInput, "There is no new version to install.", retryable: false));

            Publish(new UpdateSnapshot(UpdatePhase.Downloading, release, 0, null, false));
            var progress = new Progress(this, release);
            var downloaded = await _installer.DownloadAsync(release, progress, ct).ConfigureAwait(false);
            if (downloaded.IsCancelled)
            {
                Publish(new UpdateSnapshot(UpdatePhase.Available, release, 0, null, false));
                return Result.Cancelled();
            }

            if (downloaded.IsFailure)
                return Fail(release, "update.download.failed", downloaded.Error!);

            var started = _installer.StartInstall(downloaded.Value);
            if (started.IsFailure)
                return Fail(release, "update.install.failed", started.Error!);

            Log(DiagnosticLevel.Information, "update.install.started", "Install");
            Publish(new UpdateSnapshot(UpdatePhase.Installing, release, 1, null, false));
            return Result.Success();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _timer?.Dispose();
        _lifetime.Cancel();
        _changes.Complete();
        // A check under way finishes against a cancelled token; the gate is left for it to release.
    }

    private void OnTimer()
    {
        if (_disposed || !IsEnabled)
            return;
        var previous = Volatile.Read(ref _pendingCheck);
        if (!previous.IsCompleted)
            return; // one check at a time
        Volatile.Write(ref _pendingCheck, CheckGuardedAsync());
    }

    private async Task CheckGuardedAsync()
    {
        try
        {
            await CheckAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Log(DiagnosticLevel.Error, "update.check.crashed", "Check", ErrorCode.Unexpected, ex.GetType());
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        // Turned off: whatever was offered goes away, and the timer finds the switch off from now on.
        if (e.Previous.Updates.Check && !e.Current.Updates.Check && Current.Phase is UpdatePhase.Available or UpdatePhase.Failed)
            Publish(UpdateSnapshot.Initial);
    }

    private Result Fail(ReleaseInfo release, string eventId, OperationError error)
    {
        Log(DiagnosticLevel.Warning, eventId, "Install", error.Code);
        Publish(new UpdateSnapshot(UpdatePhase.Failed, release, 0, error, false));
        return Result.Failure(error);
    }

    private async Task<bool> EnterAsync(CancellationToken ct)
    {
        if (_disposed)
            return false;
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

    private void Publish(UpdateSnapshot snapshot)
    {
        if (Current == snapshot)
            return;
        Volatile.Write(ref _current, snapshot);
        _changes.Publish(snapshot);
    }

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? code = null, Type? exceptionType = null) =>
        _log?.Write(new DiagnosticEvent(level, eventId, Component, operation, code, ExceptionType: exceptionType));

    /// <summary>Reports whole percents only, so a fast download does not flood the UI.</summary>
    private sealed class Progress(UpdateService owner, ReleaseInfo release) : IProgress<double>
    {
        private int _lastPercent = -1;

        public void Report(double value)
        {
            var percent = (int)Math.Clamp(Math.Floor(value * 100), 0, 100);
            if (percent == _lastPercent)
                return;
            _lastPercent = percent;
            if (owner.Current.Phase == UpdatePhase.Downloading)
                owner.Publish(new UpdateSnapshot(UpdatePhase.Downloading, release, percent / 100.0, null, false));
        }
    }
}
