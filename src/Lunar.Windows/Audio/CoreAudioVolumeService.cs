using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Reactive;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Audio;

/// <summary>
/// Master volume and mute of the default output device through Windows Core Audio (BE-10, ADR-0002, ADR-0006).
///
/// Behaviour:
/// - Reads and writes run off the caller's thread, one at a time, so the UI never blocks on a slow driver.
/// - No output device at all is a state, not an error: <see cref="GetAsync"/> succeeds with
///   <see cref="VolumeState.Unavailable"/>, and a write fails with <see cref="ErrorCode.DeviceUnavailable"/>.
/// - A device that disappears or resets under a call is reopened once and the call retried; if that fails too the result
///   is the same as no device. Windows refusing a call is <see cref="ErrorCode.AccessDenied"/>; anything else is
///   <see cref="ErrorCode.Unexpected"/>. Only an HRESULT, never a device name, reaches the log.
/// - When the default device changes (or the current one is removed) the service switches to the new one by itself and
///   publishes its state, or <see cref="VolumeState.Unavailable"/> when none is left.
/// - <see cref="Changes"/> carries every change that reaches the endpoint, including ones this service made, one
///   state at a time and never the same state twice in a row. Callers that must not react to their own writes (the
///   BE-11 use cases) compare against what they just set.
/// - A successful write means Windows accepted it; the resulting level is whatever the driver reports afterwards (many
///   drivers round to their own steps), so read it back or watch <see cref="Changes"/>.
/// Thread-safe. Disposal completes the observers, releases every COM object and is idempotent.
/// </summary>
public sealed class CoreAudioVolumeService : IVolumeService, IDisposable
{
    private const string Component = "Audio";

    private readonly IAudioBackend _backend;
    private readonly IDiagnosticLog? _log;
    private readonly StateBroadcaster<VolumeState> _changes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();

    private IAudioEndpoint? _endpoint;
    private VolumeState? _lastPublished;
    private bool _disposed;
    private int _refreshRequested;
    private int _refreshRunning;

    public CoreAudioVolumeService(IDiagnosticLog? log = null)
        : this(CreateBackend(log), log)
    {
    }

    internal CoreAudioVolumeService(IAudioBackend backend, IDiagnosticLog? log = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _log = log;
        _backend.DefaultDeviceChanged += OnDefaultDeviceChanged;
    }

    public IObservable<VolumeState> Changes => _changes;

    public async Task<Result<VolumeState>> GetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<VolumeState>();
        if (IsDisposed)
            return Result.Failure<VolumeState>(Closed);

        return await RunAsync(() => ReadState(), ct).ConfigureAwait(false);
    }

    public async Task<Result> SetVolumeAsync(float scalar, CancellationToken ct)
    {
        if (float.IsNaN(scalar) || scalar < 0f || scalar > 1f)
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "Volume must be between 0 and 1."));
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        if (IsDisposed)
            return Result.Failure(Closed);

        var result = await RunAsync(() => Write(endpoint => endpoint.SetVolume(scalar)), ct).ConfigureAwait(false);
        return ToResult(result);
    }

    public async Task<Result> SetMuteAsync(bool muted, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        if (IsDisposed)
            return Result.Failure(Closed);

        var result = await RunAsync(() => Write(endpoint => endpoint.SetMute(muted)), ct).ConfigureAwait(false);
        return ToResult(result);
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        // Wait for an operation that is already talking to the device, then release everything under the same gate.
        await _gate.WaitAsync().ConfigureAwait(false);
        FinishDispose();
    }

    /// <summary>
    /// Synchronous disposal for containers that dispose singletons with <c>Dispose()</c> at shutdown. It waits, bounded by
    /// the one device call that may be running, for the same teardown as <see cref="DisposeAsync"/>.
    /// </summary>
    public void Dispose()
    {
        if (!BeginDispose())
            return;

        _lifetime.Cancel();
        _gate.Wait();
        FinishDispose();
    }

    private bool BeginDispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
                return false;
            _disposed = true;
        }

        _backend.DefaultDeviceChanged -= OnDefaultDeviceChanged;
        return true;
    }

    // Called holding the gate.
    private void FinishDispose()
    {
        try
        {
            CloseEndpoint();
            _backend.Dispose();
        }
        finally
        {
            _gate.Release();
        }

        _changes.Complete();
        _lifetime.Dispose();
    }

    private bool IsDisposed
    {
        get
        {
            lock (_stateLock)
                return _disposed;
        }
    }

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The volume service is closed.");

    private static OperationError NoDevice => new(ErrorCode.DeviceUnavailable, "No audio output device is available.", retryable: true);

    private static IAudioBackend CreateBackend(IDiagnosticLog? log)
    {
        try
        {
            return new ComAudioBackend();
        }
        catch (AudioException ex)
        {
            // Without Core Audio the service still works as "no device"; the reason is logged as an HRESULT class only.
            log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "audio.backend.failed", Component, "Start", ErrorCode.DeviceUnavailable, ExceptionType: ex.GetType()));
            return new MissingAudioBackend();
        }
    }

    // ---- operations -------------------------------------------------------------------------------------------------

    // Runs one serialized operation on the thread pool. The wait for the gate honours the caller's token; an operation
    // that already started is not interrupted (a COM call cannot be), but its result is dropped if the token fired.
    private async Task<Result<T>> RunAsync<T>(Func<Result<T>> operation, CancellationToken ct)
    {
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<T>();
        }

        try
        {
            if (IsDisposed)
                return Result.Failure<T>(Closed);
            var result = await Task.Run(operation, CancellationToken.None).ConfigureAwait(false);
            return ct.IsCancellationRequested ? Result.Cancelled<T>() : result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Result<VolumeState> ReadState()
    {
        var read = WithEndpoint(endpoint => endpoint.Read());
        if (read.IsSuccess)
        {
            var state = ToState(read.Value);
            Publish(state);
            return Result.Success(state);
        }

        // No usable device is reported as a state, so the UI can show it disabled; only a refusal stays an error.
        if (read.Error!.Code == ErrorCode.DeviceUnavailable)
        {
            Publish(VolumeState.Unavailable);
            return Result.Success(VolumeState.Unavailable);
        }

        return Result.Failure<VolumeState>(read.Error);
    }

    private Result<bool> Write(Action<IAudioEndpoint> write) =>
        WithEndpoint(endpoint =>
        {
            write(endpoint);
            return true;
        });

    private static Result ToResult(Result<bool> result) =>
        result.IsSuccess ? Result.Success() : result.IsCancelled ? Result.Cancelled() : Result.Failure(result.Error!);

    // Runs a call on the current endpoint (opening it first), and when the device was gone reopens it and tries once more.
    private Result<T> WithEndpoint<T>(Func<IAudioEndpoint, T> call)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var endpoint = _endpoint ??= OpenEndpoint();
                if (endpoint is null)
                    return Result.Failure<T>(NoDevice);

                return Result.Success(call(endpoint));
            }
            catch (AudioException ex) when (ex.Failure == AudioFailure.DeviceGone)
            {
                CloseEndpoint();
                if (attempt == 1)
                {
                    LogFailure("audio.device.gone", ex);
                    return Result.Failure<T>(NoDevice);
                }
            }
            catch (AudioException ex) when (ex.Failure == AudioFailure.AccessDenied)
            {
                LogFailure("audio.access.denied", ex);
                return Result.Failure<T>(new OperationError(ErrorCode.AccessDenied, "Windows did not allow the volume to be changed."));
            }
            catch (AudioException ex)
            {
                CloseEndpoint();
                LogFailure("audio.call.failed", ex);
                return Result.Failure<T>(new OperationError(ErrorCode.Unexpected, "The volume could not be read or changed.", retryable: true));
            }
        }

        return Result.Failure<T>(NoDevice);
    }

    private IAudioEndpoint? OpenEndpoint()
    {
        var endpoint = _backend.OpenDefault();
        if (endpoint is not null)
            endpoint.Changed += OnEndpointChanged;
        return endpoint;
    }

    // Callers hold the gate.
    private void CloseEndpoint()
    {
        var endpoint = _endpoint;
        _endpoint = null;
        if (endpoint is null)
            return;
        endpoint.Changed -= OnEndpointChanged;
        endpoint.Dispose();
    }

    // ---- notifications ----------------------------------------------------------------------------------------------

    // From a COM thread: copy the data, publish, return. Never calls back into COM.
    private void OnEndpointChanged(VolumeReading reading)
    {
        if (!IsDisposed)
            Publish(ToState(reading));
    }

    private void OnDefaultDeviceChanged()
    {
        if (IsDisposed)
            return;

        // Coalesce the burst Windows sends: one refresh runs, and anything that arrives meanwhile asks for one more.
        Interlocked.Exchange(ref _refreshRequested, 1);
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0)
            return;

        _ = Task.Run(RefreshLoopAsync);
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _refreshRequested, 0) == 1 && !IsDisposed)
            {
                try
                {
                    await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    if (IsDisposed)
                        return;
                    CloseEndpoint(); // the old endpoint belongs to the old device
                    var read = WithEndpoint(endpoint => endpoint.Read());
                    Publish(read.IsSuccess ? ToState(read.Value) : VolumeState.Unavailable);
                }
                finally
                {
                    _gate.Release();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Disposal won the race for the gate: nothing left to refresh.
        }
        finally
        {
            Interlocked.Exchange(ref _refreshRunning, 0);
            // A request that slipped in after the loop's last check starts another run.
            if (Volatile.Read(ref _refreshRequested) == 1 && !IsDisposed)
                OnDefaultDeviceChanged();
        }
    }

    // Delivers a state unless it equals the last one delivered, so observers see changes, not repeats.
    private void Publish(VolumeState state)
    {
        lock (_stateLock)
        {
            if (_disposed || Equals(_lastPublished, state))
                return;
            _lastPublished = state;
        }

        _changes.Publish(state);
    }

    private static VolumeState ToState(VolumeReading reading) =>
        new(reading.Scalar, reading.IsMuted, CapabilityStatus.Supported);

    private void LogFailure(string eventId, AudioException ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, Component, "Call", ErrorCode.DeviceUnavailable, ExceptionType: ex.GetType()));

    /// <summary>Stands in when Core Audio cannot be started at all: there is simply no device.</summary>
    private sealed class MissingAudioBackend : IAudioBackend
    {
        public event Action? DefaultDeviceChanged
        {
            add { }
            remove { }
        }

        public IAudioEndpoint? OpenDefault() => null;

        public void Dispose()
        {
        }
    }
}
