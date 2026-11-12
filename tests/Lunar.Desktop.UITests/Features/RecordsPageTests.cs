using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Notes;
using Lunar.Application.Recording;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Features.Notes;
using Lunar.Desktop.Features.Recorder;
using Lunar.Desktop.Resources;
using Lunar.Desktop.UITests.Accessibility;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Recording;
using Lunar.Domain.Shared;
using Xunit;

namespace Lunar.Desktop.UITests.Features;

// FE-34: the real page at the island's page size over the real RecordingService, with stand-ins for the microphone and the disk. A real recording,
// a real device change, a blocked microphone and Narrator are manual checks (FE-34 evidence).
public sealed class RecordsPageTests
{
    private const double PageWidth = 380;
    private const double PageHeight = 250;

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = System.IO.Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                StaDispatcher.ShutDownCurrent();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            throw new InvalidOperationException("WPF records page check failed.", failure);
    }

    private static void Pump(Func<bool> done, int seconds = 10)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (!done() && DateTime.UtcNow < end)
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            Thread.Sleep(2);
        }

        Assert.True(done(), "The page did not reach the expected state in time.");
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ContextIdle, new Action(() => { }));

    private sealed class Rig : IDisposable
    {
        public Rig()
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var dispatcher = Dispatcher.CurrentDispatcher;
            Action<Action> post = work =>
            {
                if (dispatcher.CheckAccess())
                    work();
                else
                    dispatcher.BeginInvoke(work);
            };
            Settings = new SettingsService(new Repository());
            Service = new RecordingService(Engine, Catalog, Files, Settings, new Clock(), null, Time);
            NotesService = new NotesService(new FakeNotesRepository(), new NotesPageViewModelTests.FixedTime());
            Notes = new NotesPageViewModel(NotesService, post, new ManualTimer(), new ManualTimer(), new NotesPageViewModelTests.FixedTime());
            Model = new RecordsViewModel(Service, Folders, Revealer, Launcher, post, Time);
            Host = new NotesHostViewModel(Notes, Model);
            Page = new NotesHostPage { DataContext = Host };
            Window = new Window
            {
                Width = PageWidth,
                Height = PageHeight,
                Content = Page,
                WindowStyle = WindowStyle.None,
                ShowActivated = false,
                ShowInTaskbar = false,
                Left = -20000,
                Top = -20000,
                WindowStartupLocation = WindowStartupLocation.Manual
            };
            foreach (var dictionary in new[] { "Colors.Dark.xaml", "Typography.xaml", "Spacing.xaml", "Components.xaml" })
                Window.Resources.MergedDictionaries.Add(Load("Themes", dictionary));
            Window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
            Window.Show();
            var started = Notes.InitializeAsync(CancellationToken.None);
            Pump(() => started.IsCompleted);
            Window.UpdateLayout();
        }

        public FakeEngine Engine { get; } = new();

        public FakeCatalog Catalog { get; } = new();

        public FakeFiles Files { get; } = new();

        public FakeFolders Folders { get; } = new();

        public FakeRevealer Revealer { get; } = new();

        public FakeLauncher Launcher { get; } = new();

        public ManualTime Time { get; } = new();

        public SettingsService Settings { get; }

        public RecordingService Service { get; }

        public NotesService NotesService { get; }

        public NotesPageViewModel Notes { get; }

        public RecordsViewModel Model { get; }

        public NotesHostViewModel Host { get; }

        public NotesHostPage Page { get; }

        public Window Window { get; }

        public RecordsView Records => (RecordsView)Page.FindName("RecordsView");

        public T Named<T>(string name)
            where T : class =>
            (Records.FindName(name) as T) ?? throw new InvalidOperationException(name + " is missing.");

        public void Run(ICommand command)
        {
            command.Execute(null);
            Pump(() => command is not Lunar.Desktop.Shell.AsyncRelayCommand { IsRunning: true });
            Settle();
        }

        public void Settle()
        {
            Pump();
            Pump();
            Window.UpdateLayout();
        }

        public void ShowRecords()
        {
            Host.IsRecordsShown = true;
            Pump(() => Model.Options.Count > 0);
            Settle();
        }

        public void Dispose()
        {
            Window.Close();
            Model.Dispose();
            Notes.Dispose();
            NotesService.Dispose();
            Service.Dispose();
            Settings.Dispose();
        }
    }

    private static void AssertFits(Rig rig)
    {
        foreach (var control in AutomationAudit.UsableControls(rig.Records))
        {
            var bottomRight = control.TranslatePoint(new Point(control.ActualWidth, control.ActualHeight), rig.Page);
            Assert.True(bottomRight.X <= PageWidth + 0.5, $"{AutomationAudit.NameOf(control)} ends at x={bottomRight.X}.");
            Assert.True(bottomRight.Y <= PageHeight + 0.5, $"{AutomationAudit.NameOf(control)} ends at y={bottomRight.Y}.");
        }
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(61, "01:01")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(36061, "10:01:01")]
    public void TheTimeIsMinutesThenHours(int seconds, string expected) =>
        Assert.Equal(expected, RecorderText.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ThePageStartsOnNotesAndTheButtonsSwitchTheView() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var notesView = (NotesPage)rig.Page.FindName("NotesView");
        Assert.Equal(Visibility.Visible, notesView.Visibility);
        Assert.Equal(Visibility.Collapsed, rig.Records.Visibility);

        var records = (RadioButton)rig.Page.FindName("RecordsSwitch");
        records.IsChecked = true;
        rig.Settle();

        Assert.True(rig.Host.IsRecordsShown);
        Assert.Equal(Visibility.Collapsed, notesView.Visibility);
        Assert.Equal(Visibility.Visible, rig.Records.Visibility);
        Assert.False(((RadioButton)rig.Page.FindName("NotesSwitch")).IsChecked);
    });

    [Fact]
    public void SwitchingLosesNoNoteAndDoesNotTouchARecording() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Run(rig.Notes.NewNoteCommand);
        rig.Notes.EditorText = "half a thought";
        rig.ShowRecords();
        rig.Run(rig.Model.RecordCommand);

        rig.Host.IsNotesShown = true;
        rig.Settle();
        Assert.Equal("half a thought", rig.Notes.EditorText);
        Assert.Equal(RecordingPhase.Recording, rig.Service.Current.Phase);
        rig.Host.IsRecordsShown = true;
        rig.Settle();
        Assert.Equal(RecordingPhase.Recording, rig.Service.Current.Phase);
        Assert.Equal(1, rig.Engine.Starts);
    });

    [Fact]
    public void RecordPauseResumeAndStopFollowTheRecorder() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.ShowRecords();
        Assert.Equal(Visibility.Visible, rig.Named<Button>("RecordButton").Visibility);
        Assert.Equal(Visibility.Collapsed, rig.Named<Button>("StopButton").Visibility);

        rig.Run(rig.Model.RecordCommand);
        Assert.Equal(Visibility.Collapsed, rig.Named<Button>("RecordButton").Visibility);
        Assert.Equal(Visibility.Visible, rig.Named<Button>("StopButton").Visibility);
        Assert.Equal(Strings.Get("Records.Pause"), rig.Named<Button>("PauseButton").Content);
        Assert.False(rig.Named<Button>("InputButton").IsEnabled); // the microphone cannot change mid-recording

        rig.Engine.Session!.Recorded = TimeSpan.FromSeconds(75);
        rig.Time.Fire();
        rig.Settle();
        Assert.Equal("01:15", rig.Named<TextBlock>("ElapsedText").Text);

        rig.Run(rig.Model.PauseResumeCommand);
        Assert.Equal(RecordingPhase.Paused, rig.Service.Current.Phase);
        Assert.Equal(Strings.Get("Records.Resume"), rig.Named<Button>("PauseButton").Content);
        rig.Run(rig.Model.PauseResumeCommand);
        Assert.Equal(RecordingPhase.Recording, rig.Service.Current.Phase);

        rig.Run(rig.Model.StopCommand);
        Pump(() => rig.Service.Current.Phase == RecordingPhase.Idle);
        rig.Settle();
        Assert.Equal(Strings.Format("Records.Saved", "Recording 2026-11-09 14-30-05.mp3"), rig.Named<TextBlock>("StatusText").Text);
        Assert.Equal(Visibility.Visible, rig.Named<Button>("ShowFileButton").Visibility);
        Assert.Equal("00:00", rig.Named<TextBlock>("ElapsedText").Text);
        Assert.False(rig.Time.TimerRunning);

        rig.Run(rig.Model.ShowFileCommand);
        Assert.Equal(@"C:\Music\Recording 2026-11-09 14-30-05.mp3", rig.Revealer.Shown);
    });

    [Fact]
    public void AutomaticComesFirstAndAChoiceIsKept() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Catalog.Inputs = [new AudioInput("a", "Headset", true), new AudioInput("b", "Laptop array", false)];
        rig.ShowRecords();
        rig.Run(rig.Model.TogglePickerCommand);

        Assert.Equal(Visibility.Visible, rig.Named<ScrollViewer>("InputList").Visibility);
        Assert.Equal(Visibility.Collapsed, rig.Named<Grid>("Recorder").Visibility);
        Assert.Equal([Strings.Get("Records.Automatic"), "Headset", "Laptop array"], rig.Model.Options.Select(o => o.Name));
        Assert.True(rig.Model.Options[0].IsSelected);

        rig.Run(rig.Model.Options[2].SelectCommand);

        Assert.False(rig.Model.IsPickingInput);
        Assert.Equal("Laptop array", rig.Model.CurrentInputText);
        Assert.Equal("b", rig.Service.ChosenInputId);
        Assert.True(rig.Model.Options[2].IsSelected);
    });

    [Fact]
    public void ADevicePluggedInSinceAppearsWhenTheListIsOpened() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Catalog.Inputs = [new AudioInput("a", "Headset", true)];
        rig.ShowRecords();
        rig.Catalog.Inputs = [new AudioInput("a", "Headset", true), new AudioInput("c", "USB mic", false)];

        rig.Run(rig.Model.TogglePickerCommand);

        Assert.Contains(rig.Model.Options, o => o.Name == "USB mic");
    });

    [Fact]
    public void NoMicrophoneIsSaidAndRecordIsOff() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Catalog.Inputs = [];
        rig.Host.IsRecordsShown = true;
        Pump(() => rig.Model.HasNoInputs);
        rig.Settle();

        Assert.Equal(Strings.Get("Records.ErrorNoMicrophone"), rig.Named<TextBlock>("StatusText").Text);
        Assert.False(rig.Named<Button>("RecordButton").IsEnabled);
        Assert.Empty(AutomationAudit.Problems(rig.Records));
    });

    [Fact]
    public void ABlockedMicrophoneOffersTheWindowsSetting() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.ShowRecords();
        rig.Engine.FailWith = new OperationError(ErrorCode.AccessDenied, "not for the user's eyes");

        rig.Run(rig.Model.RecordCommand);

        Assert.Equal(Strings.Get("Records.ErrorBlocked"), rig.Named<TextBlock>("StatusText").Text);
        Assert.Equal(Visibility.Visible, rig.Named<Button>("OpenSettingsButton").Visibility);
        rig.Run(rig.Model.OpenMicrophoneSettingsCommand);
        Assert.Equal([SettingsPage.Microphone], rig.Launcher.Opened);
        AssertFits(rig);
    });

    // A friend's laptop (Intel Smart Sound microphone array, 2026-11-12) said "No microphone was found" with the microphone in the list.
    [Fact]
    public void AListedMicrophoneThatWindowsWillNotOpenIsNotCalledMissingAndOffersTheSettings() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.ShowRecords();
        rig.Engine.FailWith = new OperationError(ErrorCode.DeviceUnavailable, "not for the user's eyes");

        rig.Run(rig.Model.RecordCommand);

        Assert.Equal(Strings.Get("Records.ErrorCouldNotOpen"), rig.Named<TextBlock>("StatusText").Text);
        Assert.NotEqual(Strings.Get("Records.ErrorNoMicrophone"), rig.Named<TextBlock>("StatusText").Text);
        Assert.Equal(Visibility.Visible, rig.Named<Button>("OpenSettingsButton").Visibility);
        Assert.True(rig.Model.CanRecord); // try again, or with another microphone
        AssertFits(rig);
    });

    [Theory]
    [InlineData(ErrorCode.DependencyMissing, "Records.ErrorNoEncoder")]
    [InlineData(ErrorCode.IoFailure, "Records.ErrorFolder")]
    [InlineData(ErrorCode.Unexpected, "Records.ErrorOther")]
    public void EveryFailureIsSaidInWords(ErrorCode code, string key) => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.ShowRecords();
        rig.Engine.FailWith = new OperationError(code, "not for the user's eyes");

        rig.Run(rig.Model.RecordCommand);

        Assert.Equal(Strings.Get(key), rig.Named<TextBlock>("StatusText").Text);
        Assert.Equal(Visibility.Collapsed, rig.Named<Button>("OpenSettingsButton").Visibility);
        Assert.True(rig.Named<Button>("RecordButton").IsEnabled); // it can be tried again
    });

    [Fact]
    public void ALostDeviceIsToldAlongWithWhatWasSaved() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.ShowRecords();
        rig.Run(rig.Model.RecordCommand);

        rig.Engine.Session!.RaiseFailed(new OperationError(ErrorCode.DeviceUnavailable, "gone"));
        Pump(() => rig.Service.Current.Phase == RecordingPhase.Idle);
        rig.Settle();

        var text = rig.Named<TextBlock>("StatusText").Text;
        Assert.Contains(Strings.Get("Records.NoticeDeviceLost"), text, StringComparison.Ordinal);
        Assert.Contains("Recording 2026-11-09 14-30-05.mp3", text, StringComparison.Ordinal);
    });

    [Fact]
    public void AChosenMicrophoneThatIsGoneIsToldWhileRecording() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Catalog.Inputs = [new AudioInput("a", "Headset", true)];
        var chosen = rig.Service.ChooseInputAsync("gone", CancellationToken.None);
        Pump(() => chosen.IsCompleted);
        rig.ShowRecords();

        rig.Run(rig.Model.RecordCommand);

        Assert.Equal(Strings.Get("Records.NoticeChosenMissing"), rig.Named<TextBlock>("StatusText").Text);
        Assert.Equal(Strings.Get("Records.Automatic"), rig.Model.CurrentInputText);
    });

    [Fact]
    public void TheFolderCanBeChangedAndACancelledDialogChangesNothing() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.ShowRecords();
        Assert.Equal("Lunar Recordings", rig.Model.FolderName);

        rig.Folders.Next = null;
        rig.Run(rig.Model.ChangeFolderCommand);
        Assert.Equal("Lunar Recordings", rig.Model.FolderName);

        rig.Folders.Next = @"D:\Takes\Week 45";
        rig.Run(rig.Model.ChangeFolderCommand);
        Assert.Equal("Week 45", rig.Model.FolderName);
        Assert.Equal(@"D:\Takes\Week 45", rig.Service.Folder);
    });

    [Fact]
    public void ARefusedFolderIsSaidAndNotSaved() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.ShowRecords();
        rig.Files.FolderError = new OperationError(ErrorCode.IoFailure, "no");
        rig.Folders.Next = @"\\server\share";

        rig.Run(rig.Model.ChangeFolderCommand);

        Assert.Equal(Strings.Get("Records.ErrorFolder"), rig.Named<TextBlock>("StatusText").Text);
        Assert.Equal("Lunar Recordings", rig.Model.FolderName);
    });

    [Fact]
    public void ManyReadingsOnABusyUiThreadBecomeOneUpdate() => RunOnSta(() =>
    {
        using var rig = new Rig();
        var queued = new List<Action>();
        using var model = new RecordsViewModel(rig.Service, rig.Folders, rig.Revealer, rig.Launcher, queued.Add, rig.Time);
        var start = rig.Service.StartAsync(CancellationToken.None);
        Pump(() => start.IsCompleted);
        queued.Clear();
        var last = new VoiceLevels(Enumerable.Repeat(0.75f, VoiceLevelBands.Count).ToArray());

        for (var i = 0; i < 50; i++)
            rig.Engine.Session!.RaiseLevels(new VoiceLevels(new float[VoiceLevelBands.Count]));
        rig.Engine.Session!.RaiseLevels(last);

        Assert.Single(queued);
        queued[0]();
        Assert.Same(last.Bands, model.Levels);
    });

    [Fact]
    public void EveryStateFitsAndIsNamed() => RunOnSta(() =>
    {
        using var rig = new Rig();
        rig.Catalog.Inputs = [new AudioInput("a", "A very long headset name that goes on and on for a while", true), new AudioInput("b", "Laptop array", false)];
        rig.ShowRecords();
        Assert.Empty(AutomationAudit.Problems(rig.Page));
        AssertFits(rig);

        rig.Run(rig.Model.TogglePickerCommand);
        Assert.Empty(AutomationAudit.Problems(rig.Records));
        AssertFits(rig);
        rig.Run(rig.Model.TogglePickerCommand);

        rig.Run(rig.Model.RecordCommand);
        Assert.Empty(AutomationAudit.Problems(rig.Records));
        AssertFits(rig);

        rig.Run(rig.Model.StopCommand);
        Pump(() => rig.Service.Current.Phase == RecordingPhase.Idle);
        rig.Settle();
        Assert.Empty(AutomationAudit.Problems(rig.Records));
        AssertFits(rig);
        var status = rig.Named<TextBlock>("StatusText");
        var statusEnd = status.TranslatePoint(new Point(status.ActualWidth, 0), rig.Page).X;
        Assert.True(statusEnd <= PageWidth - 40 + 0.5, $"The status text ends at {statusEnd}.");
    });

    internal sealed class Clock : IClock
    {
        public DateTimeOffset LocalNow { get; } = new(2026, 11, 9, 14, 30, 5, TimeSpan.Zero);
    }

    internal sealed class Repository : ISettingsRepository
    {
        public Task<Result<SettingsLoadResult>> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result.Success(new SettingsLoadResult(LunarSettings.Defaults, SettingsRecovery.None)));

        public Task<Result> SaveAsync(LunarSettings settings, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    internal sealed class FakeFolders : IFolderPicker
    {
        public string? Next { get; set; }

        public string? PickFolder() => Next;
    }

    internal sealed class FakeRevealer : IOutputRevealer
    {
        public string? Shown { get; private set; }

        public Task<Result> RevealAsync(string path, CancellationToken ct)
        {
            Shown = path;
            return Task.FromResult(Result.Success());
        }
    }

    internal sealed class FakeLauncher : ISystemSettingsLauncher
    {
        public List<SettingsPage> Opened { get; } = [];

        public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct)
        {
            Opened.Add(page);
            return Task.FromResult(Result.Success());
        }
    }

    internal sealed class FakeCatalog : IAudioInputCatalog
    {
        public IReadOnlyList<AudioInput> Inputs { get; set; } = [new AudioInput("a", "Headset", true)];

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task<Result<IReadOnlyList<AudioInput>>> GetInputsAsync(CancellationToken ct) => Task.FromResult(Result.Success(Inputs));
    }

    internal sealed class FakeEngine : IVoiceCaptureEngine
    {
        public int Starts { get; private set; }

        public OperationError? FailWith { get; set; }

        public FakeSession? Session { get; private set; }

        public Task<Result<IVoiceCaptureSession>> StartAsync(string? inputId, string partFilePath, CancellationToken ct)
        {
            Starts++;
            if (FailWith is not null)
                return Task.FromResult(Result.Failure<IVoiceCaptureSession>(FailWith));
            Session = new FakeSession();
            return Task.FromResult(Result.Success<IVoiceCaptureSession>(Session));
        }
    }

    internal sealed class FakeSession : IVoiceCaptureSession
    {
        public TimeSpan Recorded { get; set; }

        public event Action<VoiceLevels>? LevelsChanged;

        public event Action<OperationError>? Failed;

        public void RaiseLevels(VoiceLevels levels) => LevelsChanged?.Invoke(levels);

        public void RaiseFailed(OperationError error) => Failed?.Invoke(error);

        public Task<Result> PauseAsync(CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> ResumeAsync(CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> FinishAsync(CancellationToken ct) => Task.FromResult(Result.Success());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal sealed class FakeFiles : IRecordingFiles
    {
        public string DefaultFolder => @"C:\Music\Lunar Recordings";

        public OperationError? FolderError { get; set; }

        public Result ValidateFolder(string folder) => FolderError is null ? Result.Success() : Result.Failure(FolderError);

        public Result<long> GetFreeBytes(string folder) => Result.Success(long.MaxValue);

        public Task<Result<string>> ReservePartAsync(string folder, string baseName, CancellationToken ct) => Task.FromResult(Result.Success(folder + @"\~part.lunar-part"));

        public Task<Result<SavedRecording>> CommitAsync(string partPath, string baseName, CancellationToken ct) =>
            Task.FromResult(Result.Success(new SavedRecording(@"C:\Music\" + baseName + ".mp3", baseName + ".mp3")));

        public Task AbandonAsync(string partPath, CancellationToken ct) => Task.CompletedTask;

        public Task<Result<RecoveredRecording?>> RecoverAsync(CancellationToken ct) => Task.FromResult(Result.Success<RecoveredRecording?>(null));
    }

    internal sealed class ManualTime : TimeProvider
    {
        private readonly List<ManualTimer2> _timers = [];

        public bool TimerRunning => _timers.Any(t => !t.IsDisposed && t.IsTick);

        public void Fire()
        {
            foreach (var timer in _timers.Where(t => !t.IsDisposed && t.IsTick).ToList())
                timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer2(callback, state, period == RecordsViewModel.TickInterval, period);
            _timers.Add(timer);
            return timer;
        }

        /// <summary>Fires every live timer created with this period.</summary>
        public void FireTicks(TimeSpan period)
        {
            foreach (var timer in _timers.Where(t => !t.IsDisposed && t.Period == period).ToList())
                timer.Fire();
        }

        private sealed class ManualTimer2(TimerCallback callback, object? state, bool isTick, TimeSpan period) : ITimer
        {
            public bool IsTick { get; } = isTick;

            public TimeSpan Period { get; } = period;

            public bool IsDisposed { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => IsDisposed = true;

            public ValueTask DisposeAsync()
            {
                IsDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }
}
