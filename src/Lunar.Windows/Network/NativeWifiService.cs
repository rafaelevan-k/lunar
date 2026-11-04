using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Shared;

namespace Lunar.Windows.Network;

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
    private readonly RadioConfirmOptions _options;
    private readonly ServiceCore<WifiState> _core;

    private bool _toggleDenied;

    public NativeWifiService(IDiagnosticLog? log = null)
        : this(new NativeWlanBackend(), log)
    {
    }

    internal NativeWifiService(IWlanBackend backend, IDiagnosticLog? log = null, RadioConfirmOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _log = log;
        _options = options ?? RadioConfirmOptions.Default;
        if (_options.ConfirmAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one confirmation read is required.");

        _core = new ServiceCore<WifiState>(
            refresh: () =>
            {
                _ = ReadState();
                return Task.CompletedTask;
            },
            detach: () => _backend.Changed -= OnBackendChanged,
            release: _backend.Dispose);
        _backend.Changed += OnBackendChanged;
    }

    public IObservable<WifiState> Changes => _core.Changes;

    public async Task<Result<WifiState>> GetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<WifiState>();
        if (_core.IsDisposed)
            return Result.Failure<WifiState>(Closed);

        if (!await _core.EnterAsync(ct).ConfigureAwait(false))
            return ct.IsCancellationRequested ? Result.Cancelled<WifiState>() : Result.Failure<WifiState>(Closed);

        try
        {
            if (_core.IsDisposed)
                return Result.Failure<WifiState>(Closed);

            var read = await Task.Run(ReadState, CancellationToken.None).ConfigureAwait(false);
            return ct.IsCancellationRequested ? Result.Cancelled<WifiState>() : read;
        }
        finally
        {
            _core.Exit();
        }
    }

    public async Task<Result> SetEnabledAsync(bool enabled, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        if (_core.IsDisposed)
            return Result.Failure(Closed);

        if (!await _core.EnterAsync(ct).ConfigureAwait(false))
            return ct.IsCancellationRequested ? Result.Cancelled() : Result.Failure(Closed);

        try
        {
            if (_core.IsDisposed)
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
            _core.Exit();
        }
    }

    public ValueTask DisposeAsync() => _core.DisposeAsync();

    /// <summary>Synchronous disposal for containers that dispose singletons with <c>Dispose()</c> at shutdown.</summary>
    public void Dispose() => _core.Dispose();

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The Wi-Fi service is closed.");

    // ---- operations -------------------------------------------------------------------------------------------------

    // Reads the adapters and publishes the state. Callers hold the gate.
    private Result<WifiState> ReadState()
    {
        try
        {
            var state = Apply(WifiStateMapper.Map(_backend.Read()));
            _core.Publish(state);
            return Result.Success(state);
        }
        catch (WlanException ex)
        {
            var state = FromFailure(ex.Failure);
            if (state is not null)
            {
                LogFailure("wifi.read." + ex.Failure, ex);
                _core.Publish(state);
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
            var last = _core.Last;
            if (last is { CanToggle: true })
                _core.Publish(last with { CanToggle = false });
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
    private Task<Result> ConfirmAsync(bool enabled, CancellationToken ct)
    {
        var wanted = enabled ? RadioState.Enabled : RadioState.Disabled;
        return ServiceCore<WifiState>.ConfirmAsync(
            async () =>
            {
                var read = await Task.Run(ReadState, CancellationToken.None).ConfigureAwait(false);
                return read.IsSuccess && read.Value.Radio == wanted;
            },
            _options,
            "Windows accepted the change but Wi-Fi did not switch.",
            ct);
    }

    // From a wlanapi thread: only asks for a refresh. Never calls back into wlanapi.
    private void OnBackendChanged() => _core.RequestRefresh();

    private void LogFailure(string eventId, WlanException ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, Component, "Call", ErrorCode.DeviceUnavailable, ExceptionType: ex.GetType()));
}
