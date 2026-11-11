using System.ComponentModel;
using System.Globalization;
using System.Windows.Input;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Focus;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Domain.Focus;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Pomodoro;

/// <summary>One preset button of the Pomodoro page.</summary>
public sealed record MinutePreset(int Minutes, string Text, string Name, bool IsSelected, ICommand SelectCommand);

/// <summary>
/// The Pomodoro page (FE-36, ADR-0023). It only talks to <see cref="PomodoroService"/> and to the Settings launcher. It never says Do Not Disturb is on
/// unless the service confirmed it. Create and use it on the UI thread.
/// </summary>
public sealed class PomodoroPageViewModel : INotifyPropertyChanged, IObserver<PomodoroSnapshot>, IDisposable
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private const string Component = "PomodoroPage";

    private readonly PomodoroService _pomodoro;
    private readonly ISystemSettingsLauncher _settings;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _subscription;
    private PomodoroSnapshot _snapshot;
    private int _minutes;
    private string _timeText = string.Empty;
    private ITimer? _ticker;
    private bool _disposed;

    public PomodoroPageViewModel(PomodoroService pomodoro, ISystemSettingsLauncher settings, Action<Action> post, TimeProvider? time = null, IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(pomodoro);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        _pomodoro = pomodoro;
        _settings = settings;
        _post = post;
        _time = time ?? TimeProvider.System;
        _log = log;
        _snapshot = pomodoro.Current;
        _minutes = FocusTimerRules.IsValid(pomodoro.LastMinutes) ? pomodoro.LastMinutes : FocusTimerRules.DefaultMinutes;

        var lifetime = _lifetime.Token;
        StartCommand = new AsyncRelayCommand(ct => _pomodoro.StartAsync(_minutes, ct), () => CanStart, OnFault, lifetime);
        PauseResumeCommand = new AsyncRelayCommand(ct => IsPaused ? _pomodoro.ResumeAsync(ct) : _pomodoro.PauseAsync(ct), null, OnFault, lifetime);
        StopCommand = new AsyncRelayCommand(_ => _pomodoro.StopAsync(), null, OnFault, lifetime);
        LessCommand = new RelayCommand(() => SetMinutes(StepDown(_minutes)), () => CanChooseMinutes && _minutes > FocusTimerRules.MinMinutes);
        MoreCommand = new RelayCommand(() => SetMinutes(StepUp(_minutes)), () => CanChooseMinutes && _minutes < FocusTimerRules.MaxMinutes);
        OpenSettingsCommand = new AsyncRelayCommand(ct => _settings.OpenAsync(SettingsPage.Focus, ct), null, OnFault, lifetime);

        _subscription = pomodoro.Changes.Subscribe(this);
        Apply(_snapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand StartCommand { get; }

    public ICommand PauseResumeCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand LessCommand { get; }

    public ICommand MoreCommand { get; }

    public ICommand OpenSettingsCommand { get; }

    public bool IsActive => FocusTimerRules.IsActive(_snapshot.Phase);

    public bool IsPaused => _snapshot.Phase == PomodoroPhase.Paused;

    /// <summary>The time ran out (not stopped by hand); the island celebrates it once (FE-37).</summary>
    public bool IsFinished => _snapshot.Phase == PomodoroPhase.Finished;

    public bool CanStart => !IsActive;

    public bool CanChooseMinutes => !IsActive;

    public int Minutes => _minutes;

    public IReadOnlyList<MinutePreset> Presets => FocusTimerRules.PresetMinutes
        .Select(m => new MinutePreset(m, m.ToString(CultureInfo.CurrentCulture), Strings.Format("Pomodoro.PresetName", m), m == _minutes, new RelayCommand(() => SetMinutes(m), () => CanChooseMinutes)))
        .ToList();

    /// <summary>The time left while a session runs, otherwise the length that Start would use.</summary>
    public string TimeText
    {
        get => _timeText;
        private set => Set(ref _timeText, value, nameof(TimeText));
    }

    public string TimeName => IsActive ? Strings.Format("Pomodoro.TimeLeftName", TimeText) : Strings.Format("Pomodoro.LengthName", _minutes);

    public string PauseResumeText => IsPaused ? Strings.Get("Pomodoro.Resume") : Strings.Get("Pomodoro.Pause");

    /// <summary>The one honest line about Do Not Disturb (ADR-0023).</summary>
    public string FocusText => _snapshot.Phase switch
    {
        PomodoroPhase.Finished => Strings.Get("Pomodoro.Finished"),
        PomodoroPhase.Idle => Strings.Get("Pomodoro.Hint"),
        PomodoroPhase.Paused when _snapshot.FocusStatus != FocusModeStatus.AlreadyOn => Strings.Get("Pomodoro.PausedOff"),
        _ => _snapshot.FocusStatus switch
        {
            FocusModeStatus.TurningOn => Strings.Get("Pomodoro.TurningOn"),
            FocusModeStatus.On => Strings.Get("Pomodoro.On"),
            FocusModeStatus.AlreadyOn => Strings.Get("Pomodoro.AlreadyOn"),
            FocusModeStatus.Unavailable when _snapshot.FocusError?.Code == ErrorCode.Unsupported => Strings.Get("Pomodoro.Unsupported"),
            FocusModeStatus.Unavailable => Strings.Get("Pomodoro.Unavailable"),
            _ => Strings.Get("Pomodoro.Off")
        }
    };

    public bool ShowOpenSettings => IsActive && _snapshot.FocusStatus == FocusModeStatus.Unavailable;

    /// <summary>Played once as the egg turns sunny-side up; nothing otherwise.</summary>
    public IReadOnlyList<IReadOnlyList<string>>? EggIntroFrames => _snapshot.Phase == PomodoroPhase.Finished ? EggArt.Cracking : null;

    /// <summary>A whole egg, or a sunny-side-up egg once the time is over.</summary>
    public IReadOnlyList<string> EggRows => _snapshot.Phase == PomodoroPhase.Finished ? EggArt.Sunny : EggArt.Whole;

    /// <summary>The egg hops only while the time runs (the view also stops it when hidden or under reduced motion).</summary>
    public bool IsEggBouncing => _snapshot.Phase == PomodoroPhase.Running;

    public void OnNext(PomodoroSnapshot value) => _post(() => Apply(value));

    public void OnCompleted()
    {
    }

    public void OnError(Exception error)
    {
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _subscription.Dispose();
        _ticker?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    /// <summary>Below five minutes one at a time, above it five at a time, so every length from 1 to 180 that matters is a few presses away.</summary>
    internal static int StepUp(int minutes) => Math.Min(FocusTimerRules.MaxMinutes, minutes < 5 ? minutes + 1 : ((minutes / 5) + 1) * 5);

    internal static int StepDown(int minutes) => Math.Max(FocusTimerRules.MinMinutes, minutes <= 5 ? minutes - 1 : ((minutes - 1) / 5) * 5);

    internal static string Format(TimeSpan time)
    {
        // Rounded up, so the last second shows 00:01 rather than 00:00 while it still runs.
        var whole = TimeSpan.FromSeconds(Math.Ceiling(Math.Max(0, time.TotalSeconds)));
        return whole.TotalHours >= 1
            ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", (int)whole.TotalHours, whole.Minutes, whole.Seconds)
            : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", (int)whole.TotalMinutes, whole.Seconds);
    }

    private void SetMinutes(int minutes)
    {
        if (!CanChooseMinutes || !FocusTimerRules.IsValid(minutes) || minutes == _minutes)
            return;
        _minutes = minutes;
        Raise(nameof(Minutes));
        Raise(nameof(Presets));
        Refresh();
    }

    private void Apply(PomodoroSnapshot snapshot)
    {
        if (_disposed)
            return;
        _snapshot = snapshot;
        var wanted = snapshot.Phase == PomodoroPhase.Running;
        if (wanted && _ticker is null)
        {
            _ticker = _time.CreateTimer(_ => _post(Tick), null, TickInterval, TickInterval);
        }
        else if (!wanted && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }

        foreach (var name in new[] { nameof(IsActive), nameof(IsPaused), nameof(IsFinished), nameof(CanStart), nameof(CanChooseMinutes), nameof(PauseResumeText), nameof(FocusText), nameof(ShowOpenSettings), nameof(Presets), nameof(EggIntroFrames), nameof(EggRows), nameof(IsEggBouncing) })
            Raise(name);
        Refresh();
    }

    private void Tick()
    {
        if (!_disposed && _ticker is not null)
            Refresh();
    }

    private void Refresh()
    {
        TimeText = IsActive ? Format(_pomodoro.Remaining) : Format(TimeSpan.FromMinutes(_minutes));
        Raise(nameof(TimeName));
        ((AsyncRelayCommand)StartCommand).NotifyCanExecuteChanged();
        ((RelayCommand)LessCommand).NotifyCanExecuteChanged();
        ((RelayCommand)MoreCommand).NotifyCanExecuteChanged();
    }

    private void OnFault(Exception exception) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "pomodoro.command.failed", Component, "Command", ErrorCode.Unexpected, ExceptionType: exception.GetType()));

    private bool Set<T>(ref T field, T value, string name)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        Raise(name);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
