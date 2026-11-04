using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Foundation;
using WinRadioState = Windows.Devices.Radios.RadioState;

namespace Lunar.Windows.Bluetooth;

/// <summary>
/// The real <c>Windows.Devices.Radios</c> implementation of <see cref="IBluetoothBackend"/> (ADR-0002, ADR-0013). It reads
/// only the state of Bluetooth radios and, when asked, sets it. It never lists, pairs or connects devices, never asks for
/// a device name or address, and never calls <c>Radio.RequestAccessAsync</c> (the consent request is left to the moment a
/// user asks for a change: the change itself reports a refusal).
///
/// Radios are re-enumerated on every read. Each radio object's <c>StateChanged</c> is subscribed while it is the current
/// one and unsubscribed when it is replaced or the backend is disposed; a device watcher reports radios added or removed
/// (its initial burst is ignored). Callbacks arrive on thread-pool threads and only raise <see cref="Changed"/>.
/// Calls are serialized by the owning service.
/// </summary>
internal sealed class WinRtBluetoothBackend : IBluetoothBackend
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);

    private readonly object _gate = new();
    private readonly TypedEventHandler<Radio, object> _stateChanged;
    private List<Radio> _tracked = [];
    private DeviceWatcher? _watcher;
    private bool _watcherReady;
    private bool _disposed;

    public WinRtBluetoothBackend()
    {
        _stateChanged = (_, _) => Changed?.Invoke();
    }

    public event Action? Changed;

    public async Task<IReadOnlyList<BluetoothRadioReading>> ReadAsync(CancellationToken ct)
    {
        try
        {
            var radios = await BluetoothRadiosAsync(ct).ConfigureAwait(false);
            Track(radios);
            EnsureWatcher();
            return [.. radios.Select(radio => new BluetoothRadioReading(ToMode(radio.State)))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }
    }

    public async Task<BluetoothAccess> SetAsync(bool on, CancellationToken ct)
    {
        try
        {
            var radios = await BluetoothRadiosAsync(ct).ConfigureAwait(false);
            var result = BluetoothAccess.Allowed;
            var attempted = false;
            foreach (var radio in radios)
            {
                if (radio.State is not (WinRadioState.On or WinRadioState.Off))
                    continue; // disabled by the system or unknown: an app cannot change it

                attempted = true;
                var status = await radio.SetStateAsync(on ? WinRadioState.On : WinRadioState.Off)
                    .AsTask(ct).ConfigureAwait(false);
                var access = ToAccess(status);
                if (result == BluetoothAccess.Allowed)
                    result = access;
            }

            if (!attempted)
                throw new BluetoothException(BluetoothFailure.NotSupported, 0, "No Bluetooth radio can be switched.");
            return result;
        }
        catch (BluetoothException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw Translate(ex);
        }
    }

    public void Dispose()
    {
        DeviceWatcher? watcher;
        List<Radio> tracked;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            watcher = _watcher;
            _watcher = null;
            tracked = _tracked;
            _tracked = [];
        }

        foreach (var radio in tracked)
            radio.StateChanged -= _stateChanged;

        if (watcher is not null)
        {
            watcher.Added -= OnWatcherChanged;
            watcher.Removed -= OnWatcherRemoved;
            watcher.EnumerationCompleted -= OnEnumerationCompleted;
            if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                watcher.Stop();
        }
    }

    private static async Task<List<Radio>> BluetoothRadiosAsync(CancellationToken ct)
    {
        var all = await Radio.GetRadiosAsync().AsTask(ct).ConfigureAwait(false);
        return [.. all.Where(radio => radio.Kind == RadioKind.Bluetooth)];
    }

    // Keeps StateChanged subscribed on the current radio objects only.
    private void Track(List<Radio> radios)
    {
        List<Radio> previous;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _tracked;
            _tracked = radios;
        }

        foreach (var radio in previous)
            radio.StateChanged -= _stateChanged;
        foreach (var radio in radios)
            radio.StateChanged += _stateChanged;
    }

    private void EnsureWatcher()
    {
        lock (_gate)
        {
            if (_watcher is not null || _disposed)
                return;

            var watcher = DeviceInformation.CreateWatcher(Radio.GetDeviceSelector());
            watcher.Added += OnWatcherChanged;
            watcher.Removed += OnWatcherRemoved;
            watcher.EnumerationCompleted += OnEnumerationCompleted;
            _watcher = watcher;
            watcher.Start();
        }
    }

    private void OnEnumerationCompleted(DeviceWatcher sender, object args) => _watcherReady = true;

    private void OnWatcherChanged(DeviceWatcher sender, DeviceInformation args)
    {
        if (_watcherReady)
            Changed?.Invoke();
    }

    private void OnWatcherRemoved(DeviceWatcher sender, DeviceInformationUpdate args)
    {
        if (_watcherReady)
            Changed?.Invoke();
    }

    internal static BluetoothRadioMode ToMode(WinRadioState state) => state switch
    {
        WinRadioState.On => BluetoothRadioMode.On,
        WinRadioState.Off => BluetoothRadioMode.Off,
        WinRadioState.Disabled => BluetoothRadioMode.Disabled,
        _ => BluetoothRadioMode.Unknown
    };

    internal static BluetoothAccess ToAccess(RadioAccessStatus status) => status switch
    {
        RadioAccessStatus.Allowed => BluetoothAccess.Allowed,
        RadioAccessStatus.DeniedByUser => BluetoothAccess.DeniedByUser,
        RadioAccessStatus.DeniedBySystem => BluetoothAccess.DeniedBySystem,
        _ => BluetoothAccess.Unspecified
    };

    private static BluetoothException Translate(Exception ex) => ex switch
    {
        UnauthorizedAccessException => new BluetoothException(BluetoothFailure.AccessDenied, AccessDeniedHResult, "Windows refused the radio call."),
        COMException com when com.HResult == AccessDeniedHResult => new BluetoothException(BluetoothFailure.AccessDenied, com.HResult, "Windows refused the radio call."),
        TypeLoadException or PlatformNotSupportedException or NotSupportedException or FileNotFoundException or DllNotFoundException =>
            new BluetoothException(BluetoothFailure.NotSupported, ex.HResult, "The Windows radio API is not available."),
        _ => new BluetoothException(BluetoothFailure.Other, ex.HResult, "The Windows radio call failed (0x" + ex.HResult.ToString("X8") + ").")
    };
}
