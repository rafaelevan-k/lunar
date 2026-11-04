using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System.Models;
using Lunar.Domain.Shared;
using Lunar.Windows.Shared;

namespace Lunar.Windows.Bluetooth;

/// <summary>
/// Bluetooth radio state and switch through the Windows radio API (BE-13, ADR-0002, ADR-0006, ADR-0013).
///
/// Behaviour:
/// - Reading never asks for consent, lists devices or pairs anything; only the on/off state of the radios is read.
/// - "No Bluetooth radio", "this Windows has no radio API" and "Windows refused the query" are distinguishable states of a
///   successful read (<see cref="CapabilityStatus.Unavailable"/>, <see cref="CapabilityStatus.Unsupported"/>,
///   <see cref="CapabilityStatus.PermissionDenied"/>), so the UI can show them disabled with the Settings link. Only an
///   unexpected failure is an error.
/// - <see cref="BluetoothState.CanToggle"/> is true only for a radio that is On or Off. A radio the system holds
///   "Disabled" (airplane mode, policy, a hardware key) reads as Disabled and cannot be switched. When Windows refuses a
///   change, the refusal is remembered and <c>CanToggle</c> stays false for the life of the service, so the UI falls
///   back to the Settings page.
/// - <see cref="SetEnabledAsync"/> succeeds only after the radio is read back in the requested state (bounded wait, then
///   <see cref="ErrorCode.Timeout"/>) and writes nothing when the radio already has that state. A user or system refusal
///   is <see cref="ErrorCode.AccessDenied"/>.
/// - <see cref="Changes"/> carries radio changes and radios added or removed, one state at a time and never the same state
///   twice in a row.
/// Thread-safe. Disposal completes the observers, releases the radio objects and the watcher, and is idempotent.
/// </summary>
public sealed class WindowsBluetoothService : IBluetoothService, IDisposable
{
    private const string Component = "Bluetooth";

    private readonly IBluetoothBackend _backend;
    private readonly IDiagnosticLog? _log;
    private readonly RadioConfirmOptions _options;
    private readonly RadioServiceCore<BluetoothState> _core;

    private bool _toggleDenied;

    public WindowsBluetoothService(IDiagnosticLog? log = null)
        : this(new WinRtBluetoothBackend(), log)
    {
    }

    internal WindowsBluetoothService(IBluetoothBackend backend, IDiagnosticLog? log = null, RadioConfirmOptions? options = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _log = log;
        _options = options ?? RadioConfirmOptions.Default;
        if (_options.ConfirmAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "At least one confirmation read is required.");

        _core = new RadioServiceCore<BluetoothState>(
            refresh: async () => _ = await ReadStateAsync(CancellationToken.None).ConfigureAwait(false),
            detach: () => _backend.Changed -= OnBackendChanged,
            release: _backend.Dispose);
        _backend.Changed += OnBackendChanged;
    }

    public IObservable<BluetoothState> Changes => _core.Changes;

    public async Task<Result<BluetoothState>> GetAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return Result.Cancelled<BluetoothState>();
        if (_core.IsDisposed)
            return Result.Failure<BluetoothState>(Closed);

        if (!await _core.EnterAsync(ct).ConfigureAwait(false))
            return ct.IsCancellationRequested ? Result.Cancelled<BluetoothState>() : Result.Failure<BluetoothState>(Closed);

        try
        {
            if (_core.IsDisposed)
                return Result.Failure<BluetoothState>(Closed);

            var read = await ReadStateAsync(ct).ConfigureAwait(false);
            return ct.IsCancellationRequested ? Result.Cancelled<BluetoothState>() : read;
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

            var current = await ReadStateAsync(ct).ConfigureAwait(false);
            if (current.IsCancelled)
                return Result.Cancelled();
            if (current.IsFailure)
                return Result.Failure(current.Error!);

            var refusal = Refusal(current.Value, enabled);
            if (refusal is not null)
                return refusal;
            if (IsAlready(current.Value, enabled))
                return Result.Success(); // nothing to write

            var write = await WriteAsync(enabled, ct).ConfigureAwait(false);
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

    private static OperationError Closed => new(ErrorCode.DeviceUnavailable, "The Bluetooth service is closed.");

    // ---- operations -------------------------------------------------------------------------------------------------

    // Reads the radios and publishes the state. Callers hold the gate.
    private async Task<Result<BluetoothState>> ReadStateAsync(CancellationToken ct)
    {
        try
        {
            var state = Apply(BluetoothStateMapper.Map(await _backend.ReadAsync(ct).ConfigureAwait(false)));
            _core.Publish(state);
            return Result.Success(state);
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled<BluetoothState>();
        }
        catch (BluetoothException ex)
        {
            var state = FromFailure(ex.Failure);
            if (state is not null)
            {
                LogFailure("bluetooth.read." + ex.Failure, ex);
                _core.Publish(state);
                return Result.Success(state);
            }

            LogFailure("bluetooth.read.failed", ex);
            return Result.Failure<BluetoothState>(new OperationError(ErrorCode.Unexpected, "The Bluetooth state could not be read.", retryable: true));
        }
    }

    private static BluetoothState? FromFailure(BluetoothFailure failure) => failure switch
    {
        BluetoothFailure.NotSupported => BluetoothStateMapper.NotSupported,
        BluetoothFailure.AccessDenied => BluetoothStateMapper.Denied,
        _ => null
    };

    // A change Windows refused once is not offered again.
    private BluetoothState Apply(BluetoothState state) => _toggleDenied && state.CanToggle ? state with { CanToggle = false } : state;

    // Why the switch cannot be flipped right now, or null when it can.
    private static Result? Refusal(BluetoothState state, bool enabled)
    {
        if (state.Capability == CapabilityStatus.Unsupported)
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "This Windows cannot switch Bluetooth from here."));
        if (state.Capability == CapabilityStatus.PermissionDenied)
            return Result.Failure(new OperationError(ErrorCode.AccessDenied, "Windows did not allow Bluetooth to be read or changed."));
        if (state.Capability != CapabilityStatus.Supported || state.Radio == RadioState.Unavailable)
            return Result.Failure(new OperationError(ErrorCode.DeviceUnavailable, "No Bluetooth radio is available.", retryable: true));
        if (IsAlready(state, enabled))
            return null; // fine whatever the toggle capability says: nothing will be written
        if (!state.CanToggle)
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "Bluetooth cannot be switched from here (the system holds it off, or Windows does not allow it)."));
        return null;
    }

    private static bool IsAlready(BluetoothState state, bool enabled) =>
        state.Radio == (enabled ? RadioState.Enabled : RadioState.Disabled);

    // Returns null when Windows accepted the change, otherwise the failure to hand back.
    private async Task<Result?> WriteAsync(bool enabled, CancellationToken ct)
    {
        try
        {
            var access = await _backend.SetAsync(enabled, ct).ConfigureAwait(false);
            switch (access)
            {
                case BluetoothAccess.Allowed:
                    return null;
                case BluetoothAccess.DeniedByUser or BluetoothAccess.DeniedBySystem:
                    return Denied();
                default:
                    _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "bluetooth.toggle.unspecified", Component, "Call", ErrorCode.Unexpected));
                    return Result.Failure(new OperationError(ErrorCode.Unexpected, "Bluetooth could not be switched.", retryable: true));
            }
        }
        catch (OperationCanceledException)
        {
            return Result.Cancelled();
        }
        catch (BluetoothException ex) when (ex.Failure == BluetoothFailure.AccessDenied)
        {
            LogFailure("bluetooth.toggle.denied", ex);
            return Denied();
        }
        catch (BluetoothException ex) when (ex.Failure == BluetoothFailure.NotSupported)
        {
            LogFailure("bluetooth.toggle.unsupported", ex);
            return Result.Failure(new OperationError(ErrorCode.Unsupported, "This Bluetooth radio cannot be switched from here."));
        }
        catch (BluetoothException ex)
        {
            LogFailure("bluetooth.toggle.failed", ex);
            return Result.Failure(new OperationError(ErrorCode.Unexpected, "Bluetooth could not be switched.", retryable: true));
        }
    }

    private Result Denied()
    {
        _toggleDenied = true;
        var last = _core.Last;
        if (last is { CanToggle: true })
            _core.Publish(last with { CanToggle = false });
        return Result.Failure(new OperationError(ErrorCode.AccessDenied, "Windows did not allow Bluetooth to be switched."));
    }

    // Windows accepted the change; success is only reported once the radio is seen in the requested state.
    private Task<Result> ConfirmAsync(bool enabled, CancellationToken ct)
    {
        var wanted = enabled ? RadioState.Enabled : RadioState.Disabled;
        return RadioServiceCore<BluetoothState>.ConfirmAsync(
            async () =>
            {
                var read = await ReadStateAsync(ct).ConfigureAwait(false);
                return read.IsSuccess && read.Value.Radio == wanted;
            },
            _options,
            "Windows accepted the change but Bluetooth did not switch.",
            ct);
    }

    private void OnBackendChanged() => _core.RequestRefresh();

    private void LogFailure(string eventId, BluetoothException ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, Component, "Call", ErrorCode.DeviceUnavailable, ExceptionType: ex.GetType()));
}
