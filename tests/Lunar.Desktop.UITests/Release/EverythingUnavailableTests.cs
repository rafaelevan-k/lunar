using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Lunar.Application.Abstractions.Audio;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Media;
using Lunar.Application.Abstractions.Notes;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Abstractions.Updates;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Conversion.UseCases;
using Lunar.Application.Media.Models;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Notes;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell.Models;
using Lunar.Application.System.Models;
using Lunar.Application.Tests.Fakes;
using Lunar.Application.Usage;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Features.MediaControls;
using Lunar.Desktop.Features.Notes;
using Lunar.Desktop.Features.Settings;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Desktop.Features.Usage;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.Shell;
using Lunar.Desktop.UITests.Accessibility;
using Lunar.Desktop.UITests.Features;
using Lunar.Desktop.UITests.Themes;
using Lunar.Domain.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Lunar.Desktop.UITests.Release;

// FE-26 (UI regression, "feature failures"): the whole island composed the way App composes it, from the real container, with every
// Windows feature down at once: no audio endpoint, Wi-Fi unavailable, Bluetooth unsupported, media unreadable, no Word renderer (and it
// fails to answer), unreadable displays, a Settings app that will not open, an unreadable Start with Windows entry. The pages each test
// their own failures; this proves they also hold together: the island opens, every page can be reached, nothing throws on the UI
// thread, the keyboard/automation audit still passes, the window stays on the work area, and closing lets everything go.
// Fakes stand in for Windows; this is not evidence that any Windows API works.
public sealed class EverythingUnavailableTests
{
    private sealed class NullLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Events { get; } = [];

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (Events)
                Events.Add(diagnosticEvent);
        }
    }

    private sealed class NoAudio : IVolumeService
    {
        private readonly TestObservable<VolumeState> _changes = new();

        public IObservable<VolumeState> Changes => _changes;

        public Task<Result<VolumeState>> GetAsync(CancellationToken ct) =>
            Task.FromResult(Result.Failure<VolumeState>(new OperationError(ErrorCode.DeviceUnavailable, "No audio endpoint.")));

        public Task<Result> SetVolumeAsync(float scalar, CancellationToken ct) =>
            Task.FromResult(Result.Failure(new OperationError(ErrorCode.DeviceUnavailable, "No audio endpoint.")));

        public Task<Result> SetMuteAsync(bool muted, CancellationToken ct) =>
            Task.FromResult(Result.Failure(new OperationError(ErrorCode.DeviceUnavailable, "No audio endpoint.")));

        public ValueTask DisposeAsync()
        {
            _changes.Complete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoMedia : IMediaService
    {
        private readonly TestObservable<IReadOnlyList<MediaSessionSnapshot>> _changes = new();

        public IObservable<IReadOnlyList<MediaSessionSnapshot>> Changes => _changes;

        private static Task<Result> Down() => Task.FromResult(Result.Failure(new OperationError(ErrorCode.DeviceUnavailable, "No media.")));

        public Task<Result<IReadOnlyList<MediaSessionSnapshot>>> GetSessionsAsync(CancellationToken ct) =>
            Task.FromResult(Result.Failure<IReadOnlyList<MediaSessionSnapshot>>(new OperationError(ErrorCode.DeviceUnavailable, "No media.")));

        public Task<Result> PlayPauseAsync(string sessionId, CancellationToken ct) => Down();

        public Task<Result> NextAsync(string sessionId, CancellationToken ct) => Down();

        public Task<Result> PreviousAsync(string sessionId, CancellationToken ct) => Down();

        public Task<Result> SeekAsync(string sessionId, TimeSpan position, CancellationToken ct) => Down();

        public ValueTask DisposeAsync()
        {
            _changes.Complete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoDisplays : IWindowEnvironment
    {
        public event Action? DisplaysChanged
        {
            add { }
            remove { }
        }

        public Result Start() => Result.Failure(new OperationError(ErrorCode.DeviceUnavailable, "No display events."));

        public Result<IReadOnlyList<DisplayInfo>> GetDisplays() =>
            Result.Failure<IReadOnlyList<DisplayInfo>>(new OperationError(ErrorCode.DeviceUnavailable, "Displays unreadable."));

        public void Dispose()
        {
        }
    }

    private sealed class NoFocusMode : IFocusModeControl
    {
        private static readonly OperationError Error = new(ErrorCode.Unsupported, "No quiet-hours service.");

        public Task<Result<FocusModeState>> ReadAsync(CancellationToken ct) => Task.FromResult(Result.Failure<FocusModeState>(Error));

        public Task<Result> TurnOnAsync(CancellationToken ct) => Task.FromResult(Result.Failure(Error));

        public Task<Result> RestoreAsync(string restoreToken, CancellationToken ct) => Task.FromResult(Result.Failure(Error));
    }

    private sealed class NoMarker : IFocusSessionMarker
    {
        public Task<Result<string?>> ReadAsync(CancellationToken ct) => Task.FromResult(Result.Success<string?>(null));

        public Task<Result> SaveAsync(string restoreToken, CancellationToken ct) => Task.FromResult(Result.Success());

        public Task<Result> ClearAsync(CancellationToken ct) => Task.FromResult(Result.Success());
    }

    private sealed class NoSettingsApp : ISystemSettingsLauncher
    {
        public Task<Result> OpenAsync(SettingsPage page, CancellationToken ct) =>
            Task.FromResult(Result.Failure(new OperationError(ErrorCode.Unsupported, "Settings will not open.")));
    }

    private sealed class FixedClock : ILocaleClock
    {
        public DateTimeOffset LocalNow => new(2026, 11, 7, 23, 59, 30, TimeSpan.Zero);

        public System.Globalization.CultureInfo Culture => System.Globalization.CultureInfo.GetCultureInfo("en-US");

        public TimeZoneInfo TimeZone => TimeZoneInfo.Utc;

        public void Refresh()
        {
        }
    }

    private sealed class NoPicker : IFilePicker
    {
        public IReadOnlyList<string>? PickFiles() => null;
    }

    private sealed class NoFolders : IFolderPicker
    {
        public string? PickFolder() => null;
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
            throw new InvalidOperationException("Whole-island regression check failed.", failure);
    }

    private static ResourceDictionary Load(params string[] relative)
    {
        var path = Path.Combine([ThemeTokenTests.DesktopDirectory, .. relative]);
        // The compiled app refers to its own types without an assembly name; a loose parse needs it.
        var text = File.ReadAllText(path).Replace("\"clr-namespace:Lunar.Desktop.Controls\"", "\"clr-namespace:Lunar.Desktop.Controls;assembly=Lunar.Desktop\"")
            .Replace("\"clr-namespace:Lunar.Desktop.Shell\"", "\"clr-namespace:Lunar.Desktop.Shell;assembly=Lunar.Desktop\"");
        return (ResourceDictionary)XamlReader.Parse(text);
    }

    private static void Pump(Func<bool>? until = null, int milliseconds = 300)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        do
        {
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));
            if (until?.Invoke() == true)
                return;
            Thread.Sleep(2);
        }
        while (DateTime.UtcNow < end);
    }

    private static void Wait(Task task)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!task.IsCompleted && DateTime.UtcNow < end)
            Pump(milliseconds: 5);
        Assert.True(task.IsCompleted, "Starting a page did not finish in time.");
        task.GetAwaiter().GetResult();
    }

    [Theory]
    [InlineData("Colors.Dark.xaml", false)]
    [InlineData("Colors.Light.xaml", true)]
    [InlineData("Colors.HighContrast.xaml", true)]
    public void TheIslandOpensAndEveryPageHoldsWithEveryWindowsFeatureDown(string palette, bool reduceMotion) => RunOnSta(() =>
    {
        var log = new NullLog();
        var usage = new UsagePageViewModelTests.Source();
        var startupEntry = new StartupSwitchTests.FakeRegistration { ReadFails = ErrorCode.AccessDenied };
        var uiFailures = new List<Exception>();
        Dispatcher.CurrentDispatcher.UnhandledException += (_, e) =>
        {
            uiFailures.Add(e.Exception);
            e.Handled = true;
        };

        using var services = ServiceRegistration.BuildServiceProvider(s =>
        {
            s.AddSingleton<IDiagnosticLog>(log);
            s.AddSingleton<ISettingsRepository>(new UsagePageViewModelTests.Settings());
            s.AddSingleton<ISystemThemeSource>(new ThemeApplierTests.FakeThemeSource());
            s.AddSingleton<IVolumeService>(new NoAudio());
            s.AddSingleton<IWifiService>(new FakeWifiService
            {
                State = new WifiState(CapabilityStatus.Unavailable, RadioState.Unavailable, WifiConnectivity.Unknown, CanToggle: false)
            });
            s.AddSingleton<IBluetoothService>(new FakeBluetoothService
            {
                State = new BluetoothState(CapabilityStatus.Unsupported, RadioState.Unavailable, CanToggle: false)
            });
            s.AddSingleton<IMediaService>(new NoMedia());
            s.AddSingleton<IRendererAvailability>(new StubRenderer(false, throws: true));
            s.AddSingleton<IUsageSource>(usage);
            s.AddSingleton<IOutputRevealer>(new RecordingRevealer());
            s.AddSingleton<IFilePicker>(new NoPicker());
            s.AddSingleton<IFolderPicker>(new NoFolders());
            s.AddSingleton<IWindowEnvironment>(new NoDisplays());
            s.AddSingleton<ISystemSettingsLauncher>(new NoSettingsApp());
            s.AddSingleton<ILocaleClock>(new FixedClock());
            s.AddSingleton<IStartupRegistration>(startupEntry);
            s.AddSingleton<INotesRepository>(new Lunar.Desktop.UITests.Features.FakeNotesRepository { FailLoadWith = ErrorCode.IoFailure });
            s.AddSingleton<IBrightnessService>(new Lunar.Desktop.UITests.Features.FakeBrightnessService { FailReadWith = ErrorCode.IoFailure });
            s.AddSingleton<IFocusModeControl>(new NoFocusMode());
            s.AddSingleton<IFocusSessionMarker>(new NoMarker());
            s.AddSingleton<ICaptureExclusion>(new Lunar.Desktop.UITests.Shell.CaptureExclusionGuardTests.FakeExclusion { Refuse = true, RefuseWatch = true });
            // Sprint 9 (BE-43): no microphone at all and microphone access refused; GitHub out of reach.
            s.AddSingleton<IAudioInputCatalog>(new RecordsPageTests.FakeCatalog { Inputs = [] });
            s.AddSingleton<IVoiceCaptureEngine>(new RecordsPageTests.FakeEngine { FailWith = new OperationError(ErrorCode.AccessDenied, "Microphone access is off.") });
            s.AddSingleton<IRecordingFiles>(new RecordsPageTests.FakeFiles());
            s.AddSingleton<IUpdateFeed>(new UpdatePromptTests.FakeFeed
            {
                Next = Result.Failure<ReleaseInfo?>(new OperationError(ErrorCode.DeviceUnavailable, "GitHub could not be reached.", retryable: true))
            });
            s.AddSingleton<IUpdateInstaller>(new UpdatePromptTests.FakeInstaller());
        });

        var settingsService = services.GetRequiredService<SettingsService>();
        Wait(settingsService.LoadAsync(CancellationToken.None));
        Action<Action> toUi = work => Dispatcher.CurrentDispatcher.BeginInvoke(work);

        // Composed in the order and with the parts App.OnStartup uses (no tray, single-instance or diagnostics files here).
        var viewModel = services.GetRequiredService<IslandViewModel>();
        var eyesView = new EyesView();
        var navigation = new PageNavigationViewModel(["System", "Media", "Pomodoro", "Converter", "Notes", "Usage"]);
        // Sprint 8 (BE-38): screen-sharing exclusion refused by Windows, brightness unreadable, notes unreadable.
        using var capture = new CaptureExclusionGuard(services.GetRequiredService<ICaptureExclusion>());
        capture.StartWatching();
        var brightnessService = services.GetRequiredService<IBrightnessService>();
        using var brightnessWriter = new Lunar.Application.System.UseCases.BrightnessWriter(brightnessService);
        using var brightness = new BrightnessCardViewModel(brightnessService, brightnessWriter, () => null,
            services.GetRequiredService<ISystemSettingsLauncher>(), toUi, new DispatcherOneShotTimer(), log: log);
        var system = new SystemPageViewModel(
            services.GetRequiredService<IVolumeService>(), services.GetRequiredService<IWifiService>(), services.GetRequiredService<IBluetoothService>(),
            services.GetRequiredService<ILocaleClock>(), services.GetRequiredService<ISystemSettingsLauncher>(), toUi, log)
        {
            Brightness = brightness
        };
        var media = new MediaPageViewModel(
            services.GetRequiredService<MediaSessionTracker>(), services.GetRequiredService<MediaTransport>(), services.GetRequiredService<SeekMedia>(), toUi, log);
        var queue = new ConverterQueueViewModel(
            services.GetRequiredService<StartConversion>(), services.GetRequiredService<IConversionQueue>(), services.GetRequiredService<IOutputRevealer>(), toUi, log);
        var converter = new ConverterPageViewModel(
            services.GetRequiredService<IConversionPlanner>(), services.GetRequiredService<IRendererAvailability>(),
            services.GetRequiredService<IFilePicker>(), services.GetRequiredService<IFolderPicker>(), queue, toUi, log);
        var usagePage = new UsagePageViewModel(services.GetRequiredService<UsageMonitor>(), settingsService, toUi, log);
        var pages = IslandShellView.CreatePlaceholderPages(navigation.Titles).ToList();
        pages[0] = new SystemPage { DataContext = system };
        pages[1] = new MediaPage { DataContext = media };
        var pomodoro = services.GetRequiredService<Lunar.Application.Focus.PomodoroService>();
        using var pomodoroPage = new Lunar.Desktop.Features.Pomodoro.PomodoroPageViewModel(pomodoro, services.GetRequiredService<ISystemSettingsLauncher>(), toUi, log: log);
        pages[2] = new Lunar.Desktop.Features.Pomodoro.PomodoroPage { DataContext = pomodoroPage };
        pages[3] = new ConverterPage { DataContext = converter };
        using var notes = new NotesPageViewModel(services.GetRequiredService<NotesService>(), toUi, new DispatcherOneShotTimer(), new DispatcherOneShotTimer(), log: log);
        using var records = new Lunar.Desktop.Features.Recorder.RecordsViewModel(
            services.GetRequiredService<Lunar.Application.Recording.RecordingService>(), new RecordsPageTests.FakeFolders(), new RecordsPageTests.FakeRevealer(),
            services.GetRequiredService<ISystemSettingsLauncher>(), toUi, TimeProvider.System, log);
        pages[4] = new NotesHostPage { DataContext = new NotesHostViewModel(notes, records) };
        pages[5] = new UsagePage { DataContext = usagePage };
        var swiper = new SwiperView();
        swiper.Initialize(navigation, pages, reduceMotion);
        var shell = new IslandShellView(eyesView, swiper, viewModel, reduceMotion);
        var window = new IslandWindow { DataContext = viewModel, ViewContent = shell };
        capture.Attach(window);
        foreach (var dictionary in new[] { palette, "Typography.xaml", "Spacing.xaml", "Components.xaml" })
            window.Resources.MergedDictionaries.Add(Load("Themes", dictionary));
        window.Resources.MergedDictionaries.Add(Load("Controls", "ControlStyles.xaml"));
        var position = new IslandPositionController(window, viewModel, services.GetRequiredService<IWindowEnvironment>(), log, settingsService.Current.Island);
        var transition = new IslandTransitionController(window, viewModel, reduceMotion);
        var startup = new StartupSwitch(services.GetRequiredService<IStartupRegistration>(), log);
        using var activity = new IslandActivityController(viewModel, shell, records,
            new Lunar.Desktop.Features.Recorder.RecordingPillView { DataContext = records, ExpandCommand = viewModel.ToggleCommand }, pomodoroPage,
            new Lunar.Desktop.Features.Pomodoro.FocusPillView { DataContext = pomodoroPage, ExpandCommand = viewModel.ToggleCommand });
        var updateService = services.GetRequiredService<Lunar.Application.Updates.UpdateService>();
        var exits = 0;
        using var updates = new Lunar.Desktop.Features.Updates.UpdatePromptViewModel(updateService, toUi,
            () => activity.Current != IslandActivity.Eyes, () => exits++, log);
        try
        {
            position.Start();
            window.ShowActivated = false;
            window.Show();
            using var clockLifetime = new ClockLifetime(viewModel, system);
            using var mediaLifetime = new MediaLifetime(viewModel, media);
            using var usageLifetime = new UsageLifetime(viewModel, usagePage);
            using var notesLifetime = new NotesLifetime(viewModel, notes);
            using var brightnessLifetime = new BrightnessLifetime(viewModel, navigation, brightness);
            using var guards = new ConverterGuardLink(viewModel, converter);
            using var busy = new ConverterBusyLink(viewModel, queue);
            Wait(Task.WhenAll(system.InitializeAsync(), media.InitializeAsync(), usagePage.InitializeAsync(), notes.InitializeAsync(), brightness.InitializeAsync(),
                records.RefreshInputsAsync(CancellationToken.None)));
            Wait(converter.WhenCheckedAsync());

            // Open the island and visit every page, as a keyboard user would with the page arrows.
            Assert.True(viewModel.Toggle());
            Pump(() => viewModel.State == IslandVisualState.Expanded, 3000);
            if (viewModel.State != IslandVisualState.Expanded)
                viewModel.CompleteExpansion();
            Pump();

            var workArea = DisplayInfo.Fallback.WorkArea;
            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;
            for (var index = 0; index < PageNavigationViewModel.PageCount; index++)
            {
                Assert.True(navigation.GoTo(index) || navigation.SelectedIndex == index);
                Pump();
                window.UpdateLayout();

                Assert.Equal(index, navigation.SelectedIndex);
                Assert.True(pages[index].IsVisible, $"Page {index} is not shown when selected.");
                Assert.Empty(AutomationAudit.Problems(pages[index]));
                // The island stays within the (fallback) work area: nothing pushed off screen by the error texts.
                Assert.InRange(window.Left * scale, workArea.X - 1, workArea.Right + 1);
                Assert.InRange(window.Top * scale, workArea.Y - 1, workArea.Bottom + 1);
                Assert.True((window.Left + window.ActualWidth) * scale <= workArea.Right + 1, $"Page {index}: the island runs off the right edge.");
            }

            // What each failure looks like is each page's own test; here: the features say they are down instead of pretending.
            Assert.False(startup.IsAvailable);
            Assert.False(startup.Toggle().IsSuccess);
            Assert.Equal(0, startupEntry.Changes);
            Assert.Equal(0, usage.Calls); // Usage is off by default: nothing is requested, whatever else is down
            Assert.True(notes.HasProblem); // an unreadable notes file is said in words, and nothing can be written over it
            Assert.False(notes.NewNoteCommand.CanExecute(null));
            Pump(() => brightness.IsLoaded, 3000);
            Assert.False(brightness.IsAvailable); // brightness that cannot be read: a reason and Display settings, no slider
            Assert.True(brightness.ShowSettingsLink);
            Assert.Equal("The brightness couldn't be read.", brightness.Note);
            Assert.True(capture.IsRefused); // Windows refused to hide Lunar: the Settings window says so, and the island still works
            Wait(pomodoro.StartAsync(1, CancellationToken.None)); // Do Not Disturb that Windows will not change: the timer runs and the page says so
            Pump();
            Assert.Equal(Lunar.Application.Focus.FocusModeStatus.Unavailable, pomodoro.Current.FocusStatus);
            Assert.True(pomodoroPage.ShowOpenSettings);
            Pump();
            Assert.Equal(IslandActivity.FocusCountdown, activity.Current); // the countdown still takes the collapsed island
            Wait(pomodoro.StopAsync());
            Pump();
            Assert.Equal(IslandActivity.Eyes, activity.Current);

            // No microphone: Record is off and the page says why; nothing reaches the island.
            Assert.True(records.HasNoInputs);
            Assert.False(records.CanRecord);
            Assert.True(records.IsProblem);
            Assert.False(records.ShowsInIsland);

            // GitHub out of reach: the check fails quietly and nothing is offered or shown.
            var check = updateService.CheckAsync(CancellationToken.None);
            Wait(check);
            Assert.True(check.Result.IsSuccess);
            Pump();
            Assert.Equal(Lunar.Application.Updates.UpdatePhase.Idle, updates.Phase);
            Assert.False(updates.IsPromptVisible);
            Assert.Equal(0, exits);

            Assert.True(viewModel.Collapse());
            Pump(() => viewModel.State == IslandVisualState.Idle, 3000);
            if (viewModel.State != IslandVisualState.Idle)
                viewModel.CompleteCollapse();
            Pump();
            Assert.Equal(IslandVisualState.Idle, viewModel.State);

            Assert.Empty(uiFailures);
        }
        finally
        {
            transition.Dispose();
            position.Dispose();
            shell.Dispose();
            usagePage.Dispose();
            converter.Dispose();
            queue.Dispose();
            media.Dispose();
            system.Dispose();
            window.Close();
        }
    });
}
