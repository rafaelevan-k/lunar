using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.System;
using Lunar.Application.System.Models;
using Lunar.Application.System.UseCases;
using Lunar.Desktop.Controls;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.SystemControls;

/// <summary>
/// What the System page shows and can do (FE-13): the volume with mute, the date and time, and the Wi-Fi and Bluetooth
/// cards. It reads the Application ports only (volume through <see cref="VolumeController"/>), never a Windows API, and
/// exposes plain state and commands for the XAML to bind to.
///
/// Every state a port can report has a place on the page: not yet read (placeholder), unknown, not supported, access
/// denied and no device each show their own words instead of a broken control. Port callbacks arrive on other threads
/// and are marshalled through <c>post</c> (the dispatcher in the app, inline in tests) before any property changes.
/// The volume slider is optimistic: moving it updates the page at once and the controller coalesces the writes; a refused
/// write comes back as a corrected snapshot that snaps the slider back. The echo of the page's own write is not applied to
/// the slider, so it cannot fight the user's hand.
///
/// While the hand is on the slider (<see cref="IsAdjusting"/>) an outside change of the level is held back, never applied
/// under the thumb: when the hand lets go it is applied if the user did not move the slider meanwhile, and dropped if they
/// did (their level is the newer request and has been written). Moving the slider while muted unmutes, as Windows' own
/// volume control does; moving it to zero does not mute. A refused write is authoritative and applies at once.
///
/// Owns the volume controller; does not own the other services (the container does). Use on the UI thread.
/// </summary>
public sealed class SystemPageViewModel : INotifyPropertyChanged, IDisposable
{
    private const string Component = "SystemPage";

    private readonly VolumeController _volume;
    private readonly IWifiService _wifi;
    private readonly IBluetoothService _bluetooth;
    private readonly ILocaleClock _clock;
    private readonly Action<Action> _post;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<IDisposable> _subscriptions = [];

    private Task _lastWrite = Task.CompletedTask;
    private bool _started;
    private bool _disposed;
    private bool _applying;
    private bool _isAdjusting;
    private bool _movedDuringAdjust;
    private VolumeSnapshot? _deferred;
    private bool _isVolumeLoaded;
    private string? _volumeNote;
    private double _volumePercent;
    private bool _isMuted;
    private string? _volumeError;
    private string _timeText = string.Empty;
    private string _dateText = string.Empty;

    public SystemPageViewModel(
        IVolumeService volume,
        IWifiService wifi,
        IBluetoothService bluetooth,
        ILocaleClock clock,
        ISystemSettingsLauncher settings,
        Action<Action> post,
        IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(wifi);
        ArgumentNullException.ThrowIfNull(bluetooth);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        _volume = new VolumeController(volume, clock);
        _wifi = wifi;
        _bluetooth = bluetooth;
        _clock = clock;
        _post = post;
        _log = log;

        var lifetime = _lifetime.Token;
        ToggleMuteCommand = new AsyncRelayCommand(ToggleMuteAsync, () => IsVolumeAvailable, OnFault, lifetime);
        DismissVolumeErrorCommand = new RelayCommand(() => VolumeError = null);
        DismissErrorCommand = new RelayCommand(() =>
        {
            VolumeError = null;
            Wifi!.DismissErrorCommand.Execute(null);
            Bluetooth!.DismissErrorCommand.Execute(null);
        });
        Wifi = new RadioCardViewModel(
            "Wi-Fi", "Wi-Fi",
            (enabled, ct) => wifi.SetEnabledAsync(enabled, ct),
            ct => settings.OpenAsync(SettingsPage.Wifi, ct),
            ct => RefreshWifiAsync(ct),
            OnFault, lifetime);
        Bluetooth = new RadioCardViewModel(
            "Bluetooth", "Bluetooth",
            (enabled, ct) => bluetooth.SetEnabledAsync(enabled, ct),
            ct => settings.OpenAsync(SettingsPage.Bluetooth, ct),
            ct => RefreshBluetoothAsync(ct),
            OnFault, lifetime);
        Wifi.PropertyChanged += OnCardChanged;
        Bluetooth.PropertyChanged += OnCardChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RadioCardViewModel Wifi { get; }

    public RadioCardViewModel Bluetooth { get; }

    // ---- volume -----------------------------------------------------------------------------------------------------

    /// <summary>False until the first reading arrives; the page shows a placeholder meanwhile.</summary>
    public bool IsVolumeLoaded
    {
        get => _isVolumeLoaded;
        private set => Set(ref _isVolumeLoaded, value);
    }

    /// <summary>True when there is a usable output volume; false hides the slider behind <see cref="VolumeNote"/>.</summary>
    public bool IsVolumeAvailable => _isVolumeLoaded && _volumeNote is null;

    /// <summary>Why the slider is not usable ("No audio output device", "…not supported…"), or null when it is.</summary>
    public string? VolumeNote
    {
        get => _volumeNote;
        private set
        {
            if (Set(ref _volumeNote, value))
                OnVolumeAvailabilityChanged();
        }
    }

    /// <summary>The level, 0 to 100. Setting it (the slider) writes the new level to Windows through the controller.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            var clamped = double.IsNaN(value) ? _volumePercent : Math.Clamp(value, 0, 100);
            if (Math.Abs(clamped - _volumePercent) < 0.0001)
                return;
            _volumePercent = clamped;
            OnChanged(nameof(VolumePercent));
            OnChanged(nameof(VolumeText));
            if (_applying)
                return;

            _movedDuringAdjust = true;
            var unmute = _isMuted && clamped > 0;
            if (unmute)
                IsMuted = false; // optimistic; a refused change comes back as a corrected snapshot
            _lastWrite = WriteVolumeAsync((float)(clamped / 100d), unmute);
        }
    }

    public string VolumeText => SystemPageText.VolumeText(_volumePercent, _isMuted);

    /// <summary>
    /// True while the user holds the slider (mouse, touch, keyboard). Bound by the page; outside changes of the level wait
    /// for it to become false.
    /// </summary>
    public bool IsAdjusting
    {
        get => _isAdjusting;
        set
        {
            if (_isAdjusting == value)
                return;
            _isAdjusting = value;
            OnChanged(nameof(IsAdjusting));
            if (value)
                _movedDuringAdjust = false;
            else
                EndAdjusting();
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (!Set(ref _isMuted, value))
                return;
            OnChanged(nameof(VolumeText));
            OnChanged(nameof(MuteGlyph));
            OnChanged(nameof(MuteLabel));
        }
    }

    /// <summary>Segoe icon: speaker, or speaker with a slash while muted.</summary>
    public string MuteGlyph => _isMuted ? "" : "";

    /// <summary>What pressing the mute button would do (also its accessible name).</summary>
    public string MuteLabel => _isMuted ? "Unmute" : "Mute";

    public string? VolumeError
    {
        get => _volumeError;
        private set
        {
            if (Set(ref _volumeError, value))
                OnChanged(nameof(ErrorMessage));
        }
    }

    /// <summary>The latest volume write started by the slider (for tests and for waiting at shutdown); it never faults.</summary>
    internal Task PendingWrite => _lastWrite;

    public ICommand ToggleMuteCommand { get; }

    public ICommand DismissVolumeErrorCommand { get; }

    /// <summary>
    /// The one error the page shows (the page has room for one line): the volume's, else Wi-Fi's, else Bluetooth's. Each card
    /// still keeps its own error; dismissing clears all of them.
    /// </summary>
    public string? ErrorMessage => _volumeError ?? Wifi.Error ?? Bluetooth.Error;

    public ICommand DismissErrorCommand { get; }

    // ---- clock ------------------------------------------------------------------------------------------------------

    public string TimeText
    {
        get => _timeText;
        private set => Set(ref _timeText, value);
    }

    public string DateText
    {
        get => _dateText;
        private set => Set(ref _dateText, value);
    }

    /// <summary>Re-reads the clock and the regional format. Call when the page is shown.</summary>
    public void RefreshClock()
    {
        _clock.Refresh();
        var now = _clock.LocalNow;
        TimeText = ClockFormat.FormatTime(now, _clock.Culture);
        DateText = ClockFormat.FormatDate(now, _clock.Culture);
    }

    // ---- lifetime ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts listening to the ports, then reads each once. Never throws: a port that cannot be read leaves its card showing
    /// "Unknown" with an error. Call once, after the page is created.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_started || _disposed)
            return;
        _started = true;

        // Listen first so a change that happens during the first reading is not lost.
        _subscriptions.Add(_volume.Snapshots.Subscribe(new Listener<VolumeSnapshot>(snapshot => _post(() => ApplyVolume(snapshot)))));
        _subscriptions.Add(_wifi.Changes.Subscribe(new Listener<WifiState>(state => _post(() => ApplyWifi(state)))));
        _subscriptions.Add(_bluetooth.Changes.Subscribe(new Listener<BluetoothState>(state => _post(() => ApplyBluetooth(state)))));
        RefreshClock();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        await Task.WhenAll(
            ReadVolumeAsync(linked.Token),
            ReadWifiAsync(linked.Token),
            ReadBluetoothAsync(linked.Token)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _lifetime.Cancel();
        foreach (var subscription in _subscriptions)
            subscription.Dispose();
        _subscriptions.Clear();
        _volume.Dispose();
    }

    // ---- readings ---------------------------------------------------------------------------------------------------

    private async Task ReadVolumeAsync(CancellationToken ct)
    {
        var result = await _volume.InitializeAsync(ct).ConfigureAwait(false);
        if (result.IsFailure && !result.IsCancelled)
            _post(() => ShowVolumeUnknown());
    }

    private async Task ReadWifiAsync(CancellationToken ct)
    {
        var result = await _wifi.GetAsync(ct).ConfigureAwait(false);
        if (result.IsSuccess)
            _post(() => ApplyWifi(result.Value));
        else if (!result.IsCancelled)
            _post(() => Wifi.ShowFault("Couldn't read the Wi-Fi state."));
    }

    private async Task ReadBluetoothAsync(CancellationToken ct)
    {
        var result = await _bluetooth.GetAsync(ct).ConfigureAwait(false);
        if (result.IsSuccess)
            _post(() => ApplyBluetooth(result.Value));
        else if (!result.IsCancelled)
            _post(() => Bluetooth.ShowFault("Couldn't read the Bluetooth state."));
    }

    // After a switch request: read the real state again and apply it before returning, so the card ends the request showing
    // what Windows has, never what was asked for.
    private async Task RefreshWifiAsync(CancellationToken ct)
    {
        var result = await _wifi.GetAsync(ct).ConfigureAwait(false);
        if (result.IsCancelled)
            return;
        await PostAsync(() =>
        {
            if (result.IsSuccess)
                ApplyWifi(result.Value);
            else
                Wifi.ShowFault("Couldn't read the Wi-Fi state.");
        }, ct).ConfigureAwait(false);
    }

    private async Task RefreshBluetoothAsync(CancellationToken ct)
    {
        var result = await _bluetooth.GetAsync(ct).ConfigureAwait(false);
        if (result.IsCancelled)
            return;
        await PostAsync(() =>
        {
            if (result.IsSuccess)
                ApplyBluetooth(result.Value);
            else
                Bluetooth.ShowFault("Couldn't read the Bluetooth state.");
        }, ct).ConfigureAwait(false);
    }

    // Runs work on the UI thread and completes once it has run (or the page is closing).
    private Task PostAsync(Action work, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _post(() =>
        {
            try
            {
                work();
            }
            finally
            {
                done.TrySetResult();
            }
        });
        return done.Task.WaitAsync(ct);
    }

    // ---- applying port state ----------------------------------------------------------------------------------------

    private void ApplyVolume(VolumeSnapshot snapshot)
    {
        if (_disposed)
            return;

        var state = snapshot.State;

        // The hand is on the slider: an outside level waits (mute shows at once). Anything that changes what the slider can
        // do at all, and a refused write, still apply immediately.
        if (_isAdjusting && _volumeNote is null && state.Capability == CapabilityStatus.Supported
            && snapshot.Source is VolumeChangeSource.External or VolumeChangeSource.Initial)
        {
            _deferred = snapshot;
            _applying = true;
            try
            {
                IsMuted = state.IsMuted;
            }
            finally
            {
                _applying = false;
            }

            return;
        }

        if (snapshot.Source == VolumeChangeSource.Corrected)
            _deferred = null;

        _applying = true;
        try
        {
            VolumeNote = SystemPageText.VolumeNote(state.Capability);
            IsVolumeLoaded = true;
            IsMuted = state.IsMuted;
            // The echo of this page's own write is not applied: the slider is already where the hand put it.
            if (snapshot.Source != VolumeChangeSource.Self || _volumeNote is not null)
                VolumePercent = Math.Round(state.Scalar * 100d);
            if (snapshot.Source == VolumeChangeSource.Corrected)
                OnChanged(nameof(VolumeText));
        }
        finally
        {
            _applying = false;
        }

        ((AsyncRelayCommand)ToggleMuteCommand).NotifyCanExecuteChanged();
    }

    // The hand let go: apply what was held back, unless the user moved the slider meanwhile (their level is newer).
    private void EndAdjusting()
    {
        var deferred = _deferred;
        _deferred = null;
        if (deferred is not null && !_movedDuringAdjust && !_disposed)
            ApplyVolume(deferred);
    }

    private void ShowVolumeUnknown()
    {
        if (_disposed)
            return;
        VolumeNote = SystemPageText.VolumeNote(CapabilityStatus.Unknown);
        IsVolumeLoaded = true;
        VolumeError = "Couldn't read the volume.";
        ((AsyncRelayCommand)ToggleMuteCommand).NotifyCanExecuteChanged();
    }

    private void ApplyWifi(WifiState state)
    {
        if (_disposed)
            return;
        var (text, kind) = SystemPageText.Wifi(state);
        Wifi.Show(text, kind, state.Radio == RadioState.Enabled, state.CanToggle,
            SystemPageText.OffersSettings(state.Capability, state.Radio, state.CanToggle), SystemPageText.WifiHint(state));
    }

    private void ApplyBluetooth(BluetoothState state)
    {
        if (_disposed)
            return;
        var (text, kind) = SystemPageText.Bluetooth(state);
        Bluetooth.Show(text, kind, state.Radio == RadioState.Enabled, state.CanToggle,
            SystemPageText.OffersSettings(state.Capability, state.Radio, state.CanToggle), SystemPageText.BluetoothHint(state));
    }

    // ---- actions ----------------------------------------------------------------------------------------------------

    private async Task ToggleMuteAsync(CancellationToken ct)
    {
        VolumeError = null;
        IsMuted = !_isMuted; // optimistic; a refused change comes back as a corrected snapshot
        var result = await _volume.ToggleMuteAsync(ct);
        if (result.IsFailure && !result.IsCancelled)
            VolumeError = "Couldn't change the volume.";
    }

    // Never throws: every outcome ends in state or a log entry, so the task kept in _lastWrite cannot fault.
    private async Task WriteVolumeAsync(float scalar, bool unmute)
    {
        try
        {
            VolumeError = null;
            var level = _volume.SetVolumeAsync(scalar, _lifetime.Token);
            var mute = unmute ? _volume.SetMuteAsync(false, _lifetime.Token) : Task.FromResult(Result.Success());
            var results = await Task.WhenAll(level, mute);
            if (results.Any(r => r.IsFailure && !r.IsCancelled))
                _post(() => VolumeError = "Couldn't change the volume.");
        }
        catch (OperationCanceledException)
        {
            // Closing.
        }
        catch (Exception ex)
        {
            OnFault(ex);
        }
    }

    private void OnCardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RadioCardViewModel.Error))
            OnChanged(nameof(ErrorMessage));
    }

    private void OnFault(Exception ex) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "systempage.fault", Component, "Command", ErrorCode.Unexpected, ExceptionType: ex.GetType()));

    private void OnVolumeAvailabilityChanged()
    {
        OnChanged(nameof(IsVolumeAvailable));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnChanged(name);
        if (name == nameof(IsVolumeLoaded))
            OnChanged(nameof(IsVolumeAvailable));
        return true;
    }

    private void OnChanged(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private sealed class Listener<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
