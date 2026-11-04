using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Reactive;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Network;

/// <summary>How the service waits for Windows to confirm a radio change.</summary>
internal sealed record NativeWifiOptions
{
    public static NativeWifiOptions Default { get; } = new();

    /// <summary>How many times the radio is read back after a write before giving up. The wait is bounded: attempts x interval.</summary>
    public int ConfirmAttempts { get; init; } = 10;

    public TimeSpan ConfirmInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Replaced by tests so confirmation does not take real time.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;
}

/// <summary>
/// Wi-Fi adapter, radio and connection state through Native Wi-Fi (BE-12, ADR-0002, ADR-0006).
///
/// Behaviour:
/// - Reads and writes run off the caller's thread, one at a time. No administrator rights are assumed; what Windows refuses
///   comes back as a typed result.
/// - "No Wi-Fi adapter", "WLAN service not running", "not supported on this Windows" and "refused" are states of the
///   returned <see cref="WifiState"/> (a successful read), not errors, so the UI can show them disabled. Only an
///   unexpected failure is an error.
/// - Connected means an adapter is associated with a network. An enabled radio alone never reads as connected, and the
///   SSID, the current connection, scans and profiles are never queried.
/// - <see cref="WifiState.CanToggle"/> is true only when an adapter reports its radio switches and the hardware switch is
///   not off. A radio write that Windows refuses is remembered: from then on <c>CanToggle</c> is false for the life of the
///   service, so the UI falls back to the Settings page instead of offering a switch that fails.
/// - <see cref="SetEnabledAsync"/> succeeds only after the radio is read back in the requested state; if Windows accepts
///   the write but the radio does not change within the bounded wait the result is <see cref="ErrorCode.Timeout"/>.
///   Asking for the state the radio already has writes nothing.
/// - <see cref="Changes"/> carries changes the OS announces (radio, connection, adapter arrival/removal), one state at a
///   time and never the same state twice in a row. If the WLAN service is stopped no announcements arrive; the next read
///   notices when it is back.
/// Thread-safe. Disposal completes the observers, closes the WLAN handle and is idempotent.
/// </summary>
public sealed class NativeWifiService : IWifiService, IDisposable
{
    private const string Component = "Network";

    private readonly IWlanBackend _backend;
    private readonly IDiagnosticLog? _log;
    private readonly NativeWifiOptions _options;
    private readonly StateBroadcaster<WifiState> _changes = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateLock = new();
    private readonly CancellationTokenSource _lifetime = new();

    private WifiState? _lastPublished;
    private bool _toggleDenied;
    private bool _disposed;
    private int _refreshRequested;
    private int _refreshRunning;

    public NativeWifiService(IDiagnosticLog? log = null)
        : this(new NativeWlanBackend(), log)
    {
    }

    internal NativeWifiService(IWlanBackend backend, IDiagnosticLog? log = null, NativeWifiOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _log = log;
        _options = options ?? NativeWifiOptions.Default;
        if (_options.ConfirmAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one confirmation read is required.");
        _backend.Changed += OnBackendChanged;
    }

    public IObservable<WifiState> Changes => _changes;

    public async Task<Result<WifiState>> GetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<WifiState>();
        if (IsDisposed)
            return Result.Failure<WifiState>(Closed);

        if (!await EnterAsync(ct).ConfigureAwait(false))
            return ct.IsCancellationRequested ? Result.Cancelled<WifiState>() : Result.Failure<WifiState>(Closed);

        try
        {
            if (IsDisposed)
                return Result.Failure<WifiState>(Closed);

            var read = await Task.Run(ReadState, CancellationToken.None).ConfigureAwait(false);
            return ct.IsCancellationRequested ? Result.Cancelled<WifiState>() : read;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        if (IsDisposed)
            return Result.Failure(Closed);

        if (!await EnterAsync(ct).ConfigureAwait(false))
            return ct.IsCancellationRequested ? Result.Cancelled() : Result.Failure(Closed);

        try
        {
            if (IsDisposed)
                return Result.Failure(Closed);

            var current = await Task.Run(ReadState, CancellationToken.None).ConfigureAwait(false);
            if (current.IsFailure)
                return Result.Failure(current.Error!);

            var refusal = Refusal(current.Value, enabled);
            if (refusal is not null)
                return refusal;
            if (IsAlready(current.Value, enabled))
                return Result.Success(); // nothing to write

            var write = await Task.Run(() => Write(enabled), CancellationToken.None).ConfigureAwait(false);
            if (write is not null)
                return write;

            return await ConfirmAsync(enabled, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
            return;

        await _lifetime.CancelAsync().ConfigureAwait(false);

        // Wait for a call that is already talking to Windows, then close under the same gate.
        await _gate.WaitAsync().ConfigureAwait(false);
        FinishDispose();
    }

    /// <summary>Synchronous disposal for containers that dispose singletons with <c>Dispose()</c> at shutdown.</summary>
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

        _backend.Changed -= OnBackendChanged;
        return true;
    }

    // Called holding the gate.
    private void FinishDispose()
    {
        try
        {
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

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The Wi-Fi service is closed.");

    // ---- operations -------------------------------------------------------------------------------------------------

    // The wait for the gate honours the caller's token and disposal. False means the caller should stop.
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

    // Reads the adapters and publishes the state. Callers hold the gate.
    private Result<WifiState> ReadState()
    {
        try
        {
            var state = Apply(WifiStateMapper.Map(_backend.Read()));
            Publish(state);
            return Result.Success(state);
        }
        catch (WlanException ex)
        {
            var state = FromFailure(ex.Failure);
            if (state is not null)
            {
                LogFailure("wifi.read." + ex.Failure, ex);
                Publish(state);
                return Result.Success(state);
            }

            LogFailure("wifi.read.failed", ex);
            return Result.Failure<WifiState>(new OperationError(ErrorCode.Unexpected, "The Wi-Fi state could not be read.", retryable: true));
        }
    }

    private static WifiState? FromFailure(WlanFailure failure) => failure switch
    {
        WlanFailure.ServiceUnavailable => WifiStateMapper.NoAdapter,
        WlanFailure.NotSupported => WifiStateMapper.NotSupported,
        WlanFailure.AccessDenied => WifiStateMapper.Denied,
        _ => null
    };

    // A radio write Windows refused once is not offered again.
    private WifiState Apply(WifiState state) => _toggleDenied && state.CanToggle ? state with { CanToggle = false } : state;

    // Why a switch cannot be flipped right now, or null when it can.
    private static Result? Refusal(WifiState state, bool enabled)
    {
        if (state.Capability == CapabilityStatus.Unsupported)
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "This Windows cannot switch Wi-Fi from here."));
        if (state.Capability == CapabilityStatus.PermissionDenied)
            return Result.Failure(new OperationError(ErrorCode.AccessDenied, "Windows did not allow Wi-Fi to be read or changed."));
        if (state.Capability != CapabilityStatus.Supported || state.Radio == RadioState.Unavailable)
            return Result.Failure(new OperationError(ErrorCode.DeviceUnavailable, "No Wi-Fi adapter is available.", retryable: true));
        if (IsAlready(state, enabled))
            return null; // fine whatever the toggle capability says: nothing will be written
        if (!state.CanToggle)
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "Wi-Fi cannot be switched from here (a hardware switch is off, or Windows does not allow it)."));
        return null;
    }

    private static bool IsAlready(WifiState state, bool enabled) =>
        state.Radio == (enabled ? RadioState.Enabled : RadioState.Disabled);

    // Returns null when the write was accepted, otherwise the failure to hand back.
    private Result? Write(bool enabled)
    {
        try
        {
            _backend.SetSoftwareRadio(enabled);
            return null;
        }
        catch (WlanException ex) when (ex.Failure == WlanFailure.AccessDenied)
        {
            _toggleDenied = true;
            LogFailure("wifi.toggle.denied", ex);
            var last = _lastPublishedSafe();
            if (last is { CanToggle: true })
                Publish(last with { CanToggle = false });
            return Result.Failure(new OperationError(ErrorCode.AccessDenied, "Windows did not allow Wi-Fi to be switched."));
        }
        catch (WlanException ex) when (ex.Failure == WlanFailure.NotSupported)
        {
            LogFailure("wifi.toggle.unsupported", ex);
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "This adapter does not allow its radio to be switched."));
        }
        catch (WlanException ex)
        {
            LogFailure("wifi.toggle.failed", ex);
            return Result.Failure(new OperationError(ErrorCode.Unexpected, "Wi-Fi could not be switched.", retryable: true));
        }
    }

    // Windows accepted the write; success is only reported once the radio is seen in the requested state.
    private async Task<Result> ConfirmAsync(bool enabled, CancellationToken ct)
    {
        var wanted = enabled ? RadioState.Enabled : RadioState.Disabled;
        for (var attempt = 0; attempt < _options.ConfirmAttempts; attempt++)
        {
            var read = await Task.Run(ReadState, CancellationToken.None).ConfigureAwait(false);
            if (read.IsSuccess && read.Value.Radio == wanted)
                return Result.Success();
            if (ct.IsCancellationRequested)
                return Result.Cancelled(); // the write may already have taken effect; the next read says

            try
            {
                await _options.Delay(_options.ConfirmInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled();
            }
        }

        return Result.Failure(new OperationError(ErrorCode.Timeout, "Windows accepted the change but Wi-Fi did not switch.", retryable: true));
    }

    private WifiState? _lastPublishedSafe()
    {
        lock (_stateLock)
            return _lastPublished;
    }

    // ---- notifications ----------------------------------------------------------------------------------------------

    // From a wlanapi thread: only ask for a refresh. Never calls back into wlanapi.
    private void OnBackendChanged()
    {
        if (IsDisposed)
            return;

        // Coalesce bursts: one refresh runs, and anything that arrives meanwhile asks for one more.
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
                    _ = ReadState(); // publishes; an unexpected failure keeps the last state
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
                OnBackendChanged();
        }
    }

    // Delivers a state unless it equals the last one delivered, so observers see changes, not repeats.
    private void Publish(WifiState state)
    {
        lock (_stateLock)
        {
            if (_disposed || Equals(_lastPublished, state))
                return;
            _lastPublished = state;
        }

        _changes.Publish(state);
    }

    private void LogFailure(string eventId, WlanException ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, Component, "Call", ErrorCode.DeviceUnavailable, ExceptionType: ex.GetType()));
}
