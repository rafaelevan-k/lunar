using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Recording;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Features.Recorder;

/// <summary>One line of the microphone list.</summary>
public sealed record InputOption(string Name, bool IsSelected, ICommand SelectCommand);

/// <summary>
/// The Records view (FE-34, ADR-0022). It only talks to <see cref="RecordingService"/> and to the small ports for the folder dialog, showing a file and
/// opening Windows Settings. Audio arrives on an audio thread and is passed on at most once per turn of the UI thread. Create and use it on the UI thread.
/// </summary>
public sealed class RecordsViewModel : INotifyPropertyChanged, IObserver<RecordingSnapshot>, IDisposable
{
    internal static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private const string Component = "RecordsView";

    private readonly RecordingService _recording;
    private readonly IFolderPicker _folders;
    private readonly IOutputRevealer _revealer;
    private readonly ISystemSettingsLauncher _settings;
    private readonly Action<Action> _post;
    private readonly TimeProvider _time;
    private readonly IDiagnosticLog? _log;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IDisposable _subscription;

    private RecordingSnapshot _snapshot;
    private IReadOnlyList<AudioInput>? _inputs;
    private IReadOnlyList<InputOption> _options = [];
    private IReadOnlyList<float>? _levels;
    private VoiceLevels? _latestLevels;
    private int _levelsPending;
    private ITimer? _ticker;
    private string _elapsedText = RecorderText.Elapsed(TimeSpan.Zero);
    private bool _isPickingInput;
    private bool _noticeDismissed;
    private bool _disposed;

    public RecordsViewModel(
        RecordingService recording,
        IFolderPicker folders,
        IOutputRevealer revealer,
        ISystemSettingsLauncher settings,
        Action<Action> post,
        TimeProvider? time = null,
        IDiagnosticLog? log = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        ArgumentNullException.ThrowIfNull(folders);
        ArgumentNullException.ThrowIfNull(revealer);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(post);
        _recording = recording;
        _folders = folders;
        _revealer = revealer;
        _settings = settings;
        _post = post;
        _time = time ?? TimeProvider.System;
        _log = log;
        _snapshot = recording.Current;

        var lifetime = _lifetime.Token;
        RecordCommand = new AsyncRelayCommand(RecordAsync, () => CanRecord, OnFault, lifetime);
        PauseResumeCommand = new AsyncRelayCommand(PauseResumeAsync, null, OnFault, lifetime);
        StopCommand = new AsyncRelayCommand(_ => _recording.StopAsync(), null, OnFault, lifetime);
        TogglePickerCommand = new AsyncRelayCommand(TogglePickerAsync, () => !IsRecordingActive, OnFault, lifetime);
        ChangeFolderCommand = new AsyncRelayCommand(ChangeFolderAsync, () => !IsRecordingActive, OnFault, lifetime);
        ShowFileCommand = new AsyncRelayCommand(ct => _revealer.RevealAsync(FileToShow!, ct), () => FileToShow is not null, OnFault, lifetime);
        OpenMicrophoneSettingsCommand = new AsyncRelayCommand(ct => _settings.OpenAsync(SettingsPage.Microphone, ct), null, OnFault, lifetime);
        DismissCommand = new RelayCommand(Dismiss);

        _recording.LevelsChanged += OnLevels;
        _subscription = _recording.Changes.Subscribe(this);
        Apply(_snapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ICommand RecordCommand { get; }

    public ICommand PauseResumeCommand { get; }

    public ICommand StopCommand { get; }

    public ICommand TogglePickerCommand { get; }

    public ICommand ChangeFolderCommand { get; }

    public ICommand ShowFileCommand { get; }

    public ICommand OpenMicrophoneSettingsCommand { get; }

    public ICommand DismissCommand { get; }

    public bool IsRecordingActive => RecordingRules.IsActive(_snapshot.Phase);

    public bool ShowRecord => !IsRecordingActive;

    public bool ShowTransport => _snapshot.Phase is RecordingPhase.Recording or RecordingPhase.Paused;

    public bool IsSaving => _snapshot.Phase == RecordingPhase.Saving;

    /// <summary>The collapsed island shows the recording (ADR-0024), until the file is saved.</summary>
    public bool ShowsInIsland => ShowTransport || IsSaving;

    /// <summary>Play while paused, pause while recording.</summary>
    public string PauseResumeGlyph => IsPaused ? "\uE768" : "\uE769";

    public bool IsPaused => _snapshot.Phase == RecordingPhase.Paused;

    public bool CanRecord => !IsRecordingActive && !HasNoInputs;

    public bool HasNoInputs => _inputs is { Count: 0 };

    public string PauseResumeText => IsPaused ? Strings.Get("Records.Resume") : Strings.Get("Records.Pause");

    public string PauseResumeName => IsPaused ? Strings.Get("Records.ResumeName") : Strings.Get("Records.PauseName");

    public string ElapsedText
    {
        get => _elapsedText;
        private set => Set(ref _elapsedText, value, nameof(ElapsedText));
    }

    public IReadOnlyList<float>? Levels
    {
        get => _levels;
        private set => Set(ref _levels, value, nameof(Levels));
    }

    public bool IsPickingInput
    {
        get => _isPickingInput;
        private set
        {
            if (Set(ref _isPickingInput, value, nameof(IsPickingInput)))
                Raise(nameof(ShowRecorder));
        }
    }

    public bool ShowRecorder => !IsPickingInput;

    public IReadOnlyList<InputOption> Options
    {
        get => _options;
        private set => Set(ref _options, value, nameof(Options));
    }

    public string CurrentInputText
    {
        get
        {
            var chosen = _recording.ChosenInputId;
            var input = chosen is null ? null : _inputs?.FirstOrDefault(i => i.Id == chosen);
            return input?.Name ?? Strings.Get("Records.Automatic");
        }
    }

    public string FolderName
    {
        get
        {
            var folder = _recording.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(folder);
            return name.Length > 0 ? name : folder;
        }
    }

    public string FolderPath => _recording.Folder;

    /// <summary>The one line the user must know right now: a failure, a notice, or what was just saved.</summary>
    public string? Status
    {
        get
        {
            if (HasNoInputs && !IsRecordingActive)
                return Strings.Get("Records.ErrorNoMicrophone");
            if (_noticeDismissed)
                return null;
            if (CouldNotOpen)
                return Strings.Get("Records.ErrorCouldNotOpen");
            if (_snapshot.Error is { } error)
                return RecorderText.Failure(error);
            var notice = RecorderText.Notice(_snapshot);
            if (notice is not null && _snapshot.LastSaved is { } alongside && _snapshot.Notice != RecordingNotice.ChosenInputMissing)
                return notice + " " + Strings.Format("Records.Saved", alongside.FileName);
            if (notice is not null)
                return notice;
            return _snapshot.LastSaved is { } saved && _snapshot.Phase == RecordingPhase.Idle ? Strings.Format("Records.Saved", saved.FileName) : null;
        }
    }

    public bool HasStatus => Status is not null;

    public bool IsProblem => _snapshot.Error is not null || (HasNoInputs && !IsRecordingActive);

    public bool ShowOpenMicrophoneSettings => !_noticeDismissed && (_snapshot.Error?.Code == ErrorCode.AccessDenied || CouldNotOpen);

    /// <summary>
    /// Microphones are listed, yet Windows would not open the one asked for. That is not "no microphone": the device is there, and another app, its
    /// driver or the microphone privacy settings refused it.
    /// </summary>
    private bool CouldNotOpen => _snapshot.Error?.Code == ErrorCode.DeviceUnavailable && _inputs is { Count: > 0 };

    public bool ShowFileButton => !_noticeDismissed && FileToShow is not null && !IsRecordingActive;

    private string? FileToShow => _snapshot.Notice == RecordingNotice.RecordingRecovered ? _snapshot.Recovered?.Path : _snapshot.LastSaved?.Path;

    /// <summary>The Records view came into sight: the list of microphones is read again, so a device plugged in since shows up.</summary>
    public Task RefreshInputsAsync(CancellationToken ct) => ReadInputsAsync(ct);

    public void OnNext(RecordingSnapshot value) => _post(() => Apply(value));

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
        _recording.LevelsChanged -= OnLevels;
        _ticker?.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private async Task RecordAsync(CancellationToken ct)
    {
        IsPickingInput = false;
        _noticeDismissed = false;
        await _recording.StartAsync(ct).ConfigureAwait(true);
    }

    private Task PauseResumeAsync(CancellationToken ct) => IsPaused ? _recording.ResumeAsync(ct) : _recording.PauseAsync(ct);

    private async Task TogglePickerAsync(CancellationToken ct)
    {
        if (IsPickingInput)
        {
            IsPickingInput = false;
            return;
        }

        await ReadInputsAsync(ct).ConfigureAwait(true);
        IsPickingInput = true;
    }

    private async Task ChangeFolderAsync(CancellationToken ct)
    {
        var folder = _folders.PickFolder();
        if (folder is null)
            return;
        var saved = await _recording.ChooseFolderAsync(folder, ct).ConfigureAwait(true);
        if (saved.IsFailure)
            ApplyFailure(saved.Error!);
        Raise(nameof(FolderName));
        Raise(nameof(FolderPath));
    }

    private async Task ReadInputsAsync(CancellationToken ct)
    {
        var inputs = await _recording.GetInputsAsync(ct).ConfigureAwait(true);
        if (_disposed)
            return;
        _inputs = inputs.IsSuccess ? inputs.Value : null;
        RebuildOptions();
    }

    private void RebuildOptions()
    {
        var chosen = _recording.ChosenInputId;
        var chosenIsThere = _inputs?.Any(i => i.Id == chosen) == true;
        var options = new List<InputOption> { new(Strings.Get("Records.Automatic"), chosen is null || !chosenIsThere, new AsyncRelayCommand(ct => ChooseAsync(null, ct), null, OnFault, _lifetime.Token)) };
        foreach (var input in _inputs ?? [])
            options.Add(new InputOption(input.Name, input.Id == chosen, new AsyncRelayCommand(ct => ChooseAsync(input.Id, ct), null, OnFault, _lifetime.Token)));
        Options = options;
        Raise(nameof(CurrentInputText));
        RaiseStateProperties();
    }

    private async Task ChooseAsync(string? id, CancellationToken ct)
    {
        var saved = await _recording.ChooseInputAsync(id, ct).ConfigureAwait(true);
        if (saved.IsFailure)
            ApplyFailure(saved.Error!);
        IsPickingInput = false;
        RebuildOptions();
    }

    private void ApplyFailure(OperationError error)
    {
        _noticeDismissed = false;
        Apply(_snapshot with { Error = error });
    }

    private void Dismiss()
    {
        _noticeDismissed = true;
        RaiseStateProperties();
    }

    private void Apply(RecordingSnapshot snapshot)
    {
        if (_disposed)
            return;
        var starting = snapshot.Phase == RecordingPhase.Starting;
        if (starting || snapshot.Error is not null || snapshot.Notice != RecordingNotice.None)
            _noticeDismissed = false;
        _snapshot = snapshot;
        UpdateTicker();
        ElapsedText = RecorderText.Elapsed(_recording.Recorded);
        RaiseStateProperties();
    }

    private void UpdateTicker()
    {
        var wanted = _snapshot.Phase is RecordingPhase.Recording or RecordingPhase.Paused;
        if (wanted && _ticker is null)
        {
            _ticker = _time.CreateTimer(_ => _post(Tick), null, TickInterval, TickInterval);
        }
        else if (!wanted && _ticker is not null)
        {
            _ticker.Dispose();
            _ticker = null;
        }
    }

    private void Tick()
    {
        if (!_disposed && _ticker is not null)
            ElapsedText = RecorderText.Elapsed(_recording.Recorded);
    }

    // On an audio thread. Only the newest reading matters, so a busy UI thread is never handed a queue.
    private void OnLevels(VoiceLevels levels)
    {
        Volatile.Write(ref _latestLevels, levels);
        if (Interlocked.Exchange(ref _levelsPending, 1) == 0)
            _post(ShowLatestLevels);
    }

    private void ShowLatestLevels()
    {
        Interlocked.Exchange(ref _levelsPending, 0);
        if (!_disposed)
            Levels = Volatile.Read(ref _latestLevels)?.Bands;
    }

    private void RaiseStateProperties()
    {
        foreach (var name in new[]
        {
            nameof(IsRecordingActive), nameof(ShowRecord), nameof(ShowTransport), nameof(IsSaving), nameof(ShowsInIsland), nameof(PauseResumeGlyph), nameof(IsPaused), nameof(CanRecord), nameof(HasNoInputs),
            nameof(PauseResumeText), nameof(PauseResumeName), nameof(Status), nameof(HasStatus), nameof(IsProblem), nameof(ShowOpenMicrophoneSettings), nameof(ShowFileButton)
        })
        {
            Raise(name);
        }

        foreach (var command in new[] { RecordCommand, TogglePickerCommand, ChangeFolderCommand, ShowFileCommand }.OfType<AsyncRelayCommand>())
            command.NotifyCanExecuteChanged();
    }

    private void OnFault(Exception exception) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Error, "records.command.failed", Component, "Command", ErrorCode.Unexpected, ExceptionType: exception.GetType()));

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
