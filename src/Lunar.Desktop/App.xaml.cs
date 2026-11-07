using System.Diagnostics;
using System.Windows;
using Lunar.Application.Abstractions.Conversion;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Abstractions.Usage;
using Lunar.Application.Conversion.UseCases;
using Lunar.Application.Media.UseCases;
using Lunar.Application.Notes;
using Lunar.Application.Settings;
using Lunar.Application.Settings.Models;
using Lunar.Application.System.UseCases;
using Lunar.Application.Usage;
using Lunar.Conversion.Pipeline;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Features.MediaControls;
using Lunar.Desktop.Features.Notes;
using Lunar.Desktop.Features.Settings;
using Lunar.Desktop.Features.SystemControls;
using Lunar.Desktop.Features.Usage;
using Lunar.Desktop.Navigation;
using Lunar.Desktop.Resources;
using Lunar.Desktop.Shell;
using Lunar.Desktop.Themes;
using Lunar.Domain.Shared;
using Lunar.Infrastructure.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace Lunar.Desktop;

/// <summary>
/// Startup and lifetime only (ADR-0008). A second launch asks the running instance to activate itself and exits
/// without creating a window or tray icon. Every shutdown path ends in <see cref="OnExit"/>.
/// </summary>
public partial class App : System.Windows.Application
{
    internal const int ExitStartupFailed = 1;
    internal const int ExitPrimaryUnreachable = 3;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _uptime = Stopwatch.StartNew();
    private ServiceProvider? _services;
    private IDiagnosticLog? _log;
    private IInstanceCoordinator? _instance;
    private IslandWindow? _window;
    private IslandPositionController? _position;
    private TrayController? _tray;
    private EyeAnimationController? _eyes;
    private IslandTransitionController? _transition;
    private IslandShellView? _shell;
    private SystemPageViewModel? _system;
    private BrightnessWriter? _brightnessWriter;
    private BrightnessCardViewModel? _brightness;
    private BrightnessLifetime? _brightnessLifetime;
    private ClockLifetime? _clockLifetime;
    private MediaPageViewModel? _media;
    private MediaLifetime? _mediaLifetime;
    private ConverterPageViewModel? _converter;
    private ConverterGuardLink? _converterGuards;
    private ConverterQueueViewModel? _converterQueue;
    private ConverterBusyLink? _converterBusy;
    private NotesPageViewModel? _notes;
    private NotesLifetime? _notesLifetime;
    private UsagePageViewModel? _usage;
    private UsageLifetime? _usageLifetime;
    private UsageResetNotifier? _resetToasts;
    private ResetToastPresenter? _toastPresenter;
    private SettingsService? _settingsService;
    private ThemeApplier? _theme;
    private SettingsApplier? _settingsApplier;
    private SettingsWindowLauncher? _settingsWindow;
    private StartupSwitch? _startupSwitch;
    private CaptureExclusionGuard? _capture;

    // async void is limited to this framework override and every path is guarded.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (UninstallMode.From(e.Args) is { } uninstall)
        {
            RunUninstallCleanup(uninstall);
            return;
        }

        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            // Never restart automatically. Only the exception type is recorded (ADR-0009).
            if (_log is not null)
                _log.Write(new DiagnosticEvent(DiagnosticLevel.Error, "app.startup.failed", "App", "Start",
                    ErrorCode.Unexpected, ExceptionType: ex.GetType()));
            else
                Trace.TraceError("Startup failed before logging was available: " + ex.GetType().Name);
            Shutdown(ExitStartupFailed);
        }
    }

    // ADR-0018: run by the MSI on a full uninstall only. No island, tray, logger or single-instance role: the logger would recreate the
    // data folder the user may be deleting. The installer ignores the exit code but logs it (see UninstallMode for the values).
    private void RunUninstallCleanup(UninstallMode mode)
    {
        int exitCode;
        try
        {
            // Run as LocalSystem (not as the user) this process would see the wrong registry and profile, and a question would be shown
            // where nobody can answer it. Do nothing then; the exit code records it in the installer log.
            if (System.Security.Principal.WindowsIdentity.GetCurrent().IsSystem)
            {
                Shutdown(UninstallMode.ExitRunningAsSystem);
                return;
            }

            Strings.Use(Strings.FromWindows(System.Globalization.CultureInfo.CurrentUICulture));
            // The question is a Lunar window too, so it is hidden from screen sharing like the others (ADR-0019).
            using var capture = new CaptureExclusionGuard(new Lunar.Windows.Shell.WindowsCaptureExclusion());
            capture.StartWatching();
            var deleteData = mode.MayAsk && MessageBox.Show(
                Strings.Get("Uninstall.DeleteDataQuestion"),
                Strings.Get("Uninstall.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes;
            var result = new UninstallCleanup(new StartupRegistrationService(Environment.ProcessPath ?? string.Empty),
                UninstallCleanup.DefaultDataFolder).Run(deleteData);
            exitCode = UninstallMode.ExitCodeFor(result);
        }
        catch (Exception ex)
        {
            Trace.TraceError("Uninstall cleanup failed: " + ex.GetType().Name);
            exitCode = UninstallMode.ExitUnexpected;
        }

        Shutdown(exitCode);
    }

    private async Task StartAsync()
    {
        _services = ServiceRegistration.BuildServiceProvider();
        _log = _services.GetRequiredService<IDiagnosticLog>();
        _instance = _services.GetRequiredService<IInstanceCoordinator>();

        var role = _instance.Start();
        if (role.IsFailure)
        {
            _log.Write(new DiagnosticEvent(DiagnosticLevel.Error, "instance.check.failed", "App", "Start", role.Error!.Code));
            Shutdown(ExitStartupFailed);
            return;
        }

        if (role.Value == InstanceRole.Secondary)
        {
            var activation = await _instance.RequestPrimaryActivationAsync(_lifetime.Token);
            if (activation.IsFailure)
                _log.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "instance.activation.failed", "App", "Start", activation.Error!.Code));
            else
                _log.Write(new DiagnosticEvent(DiagnosticLevel.Information, "app.start.secondary", "App", "Start"));
            Shutdown(activation.IsSuccess ? 0 : ExitPrimaryUnreachable);
            return;
        }

        // Settings never block startup: an unreadable file means defaults (the repository already logged why).
        _settingsService = _services.GetRequiredService<SettingsService>();
        await _settingsService.LoadAsync(_lifetime.Token);
        if (_services is null)
            return; // shut down while the file was being read
        var settings = _settingsService.Current;

        // The language is fixed once, before any view exists (ADR-0017): the saved choice, or Windows' display language when none is saved.
        Strings.Use(settings.Locale ?? Strings.FromWindows(System.Globalization.CultureInfo.CurrentUICulture));
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = Strings.Culture;
        Action<Action> toUi = work => { if (Dispatcher.CheckAccess()) work(); else Dispatcher.BeginInvoke(work); };

        // The palette (dark, light, Windows' choice, High Contrast) is in place before any view exists, so nothing flashes the wrong colours.
        _theme = new ThemeApplier(
            Resources,
            _services.GetRequiredService<ISystemThemeSource>(),
            () => SystemParameters.HighContrast,
            name => new ResourceDictionary { Source = new Uri("Themes/" + name, UriKind.Relative) },
            toUi);
        _theme.Apply(settings.Appearance.Theme);

        // Before any window exists: every Lunar window, tooltip, menu, picker and message box is hidden from screen sharing (ADR-0019).
        _capture = new CaptureExclusionGuard(_services.GetRequiredService<ICaptureExclusion>());
        _capture.StartWatching();

        var viewModel = _services.GetRequiredService<IslandViewModel>();
        var eyesView = new EyesView();
        // Interface motion (slides, fades, confetti) is off when the Lunar setting says so or Windows has "Show animations" off; the eyes
        // also stop when idle eye animation is disabled. The same rule is applied again whenever the setting changes (SettingsApplier).
        var motionOff = MotionPolicy.InterfaceMotionOff(settings.Appearance, SystemParameters.ClientAreaAnimation);
        _eyes = new EyeAnimationController(new DispatcherOneShotTimer(), SystemRandomSource.Instance, TimeProvider.System)
        {
            ReduceMotion = MotionPolicy.EyeMotionOff(settings.Appearance, SystemParameters.ClientAreaAnimation)
        };
        eyesView.Attach(_eyes);
        _eyes.Start();
        // Clicking the eyes toggles the island; the state machine decides and IslandTransitionController animates it.
        eyesView.ClickCommand = viewModel.ToggleCommand;
        var navigation = new PageNavigationViewModel([Strings.Get("Nav.System"), Strings.Get("Nav.Media"), Strings.Get("Nav.Converter"), Strings.Get("Nav.Notes"), Strings.Get("Nav.Usage")]);
        // Five pages; Notes sits between Converter and Usage (ADR-0020).
        // Brightness of the display the island is on (FE-32, ADR-0021); the display is the one the position controller placed the island on.
        var brightnessService = _services.GetRequiredService<IBrightnessService>();
        _brightnessWriter = new BrightnessWriter(brightnessService);
        _brightness = new BrightnessCardViewModel(
            brightnessService,
            _brightnessWriter,
            () => _position?.CurrentDisplayId,
            _services.GetRequiredService<ISystemSettingsLauncher>(),
            toUi,
            new DispatcherOneShotTimer(),
            TimeProvider.System,
            _log);
        _system = new SystemPageViewModel(
            _services.GetRequiredService<IVolumeService>(),
            _services.GetRequiredService<IWifiService>(),
            _services.GetRequiredService<IBluetoothService>(),
            _services.GetRequiredService<ILocaleClock>(),
            _services.GetRequiredService<ISystemSettingsLauncher>(),
            work => { if (Dispatcher.CheckAccess()) work(); else Dispatcher.BeginInvoke(work); },
            _log)
        {
            Brightness = _brightness
        };
        _media = new MediaPageViewModel(
            _services.GetRequiredService<MediaSessionTracker>(),
            _services.GetRequiredService<MediaTransport>(),
            _services.GetRequiredService<SeekMedia>(),
            work => { if (Dispatcher.CheckAccess()) work(); else Dispatcher.BeginInvoke(work); },
            _log);
        _converterQueue = new ConverterQueueViewModel(
            _services.GetRequiredService<StartConversion>(),
            _services.GetRequiredService<IConversionQueue>(),
            _services.GetRequiredService<IOutputRevealer>(),
            toUi,
            _log);
        _converter = new ConverterPageViewModel(
            _services.GetRequiredService<IConversionPlanner>(),
            _services.GetRequiredService<IRendererAvailability>(),
            _services.GetRequiredService<IFilePicker>(),
            _services.GetRequiredService<IFolderPicker>(),
            _converterQueue,
            toUi,
            _log);
        var pages = IslandShellView.CreatePlaceholderPages(navigation.Titles).ToList();
        pages[0] = new SystemPage { DataContext = _system };
        pages[1] = new MediaPage { DataContext = _media };
        pages[2] = new ConverterPage { DataContext = _converter };
        _usage = new UsagePageViewModel(
            _services.GetRequiredService<UsageMonitor>(),
            _settingsService,
            toUi,
            _log);
        _notes = new NotesPageViewModel(
            _services.GetRequiredService<NotesService>(),
            toUi,
            new DispatcherOneShotTimer(),
            new DispatcherOneShotTimer(),
            TimeProvider.System,
            _log);
        pages[3] = new NotesPage { DataContext = _notes };
        pages[4] = new UsagePage { DataContext = _usage };
        var swiper = new SwiperView();
        swiper.Initialize(navigation, pages, motionOff);
        _shell = new IslandShellView(eyesView, swiper, viewModel, motionOff);
        // A finished conversion makes the eyes smile and, while the island is open, throws a little confetti.
        _converterQueue.JobSucceeded += (_, _) => _shell?.Celebrate();
        _window = new IslandWindow { DataContext = viewModel, ViewContent = _shell };
        _capture.Attach(_window); // excluded when its handle is created, before it is first shown
        MainWindow = _window;
        _position = new IslandPositionController(_window, viewModel, _services.GetRequiredService<IWindowEnvironment>(), _log, settings.Island);
        _transition = new IslandTransitionController(_window, viewModel, motionOff);
        _position.Start(); // placed before it is shown; a listener failure is logged inside and the window stays placed
        _window.Show();

        // The clock starts and stops with the island.
        _clockLifetime = new ClockLifetime(viewModel, _system);
        _brightnessLifetime = new BrightnessLifetime(viewModel, navigation, _brightness);
        _mediaLifetime = new MediaLifetime(viewModel, _media);
        _usageLifetime = new UsageLifetime(viewModel, _usage);
        _notesLifetime = new NotesLifetime(viewModel, _notes); // the open note is saved when the island collapses
        // A limit that resets gets a small pop-up at the bottom right; Escape in the island closes it (it never takes focus itself).
        _resetToasts = new UsageResetNotifier(
            _services.GetRequiredService<UsageMonitor>(),
            _toastPresenter = new ResetToastPresenter(_services.GetRequiredService<IWindowEnvironment>(), settings.Island, motionOff, _log, _capture),
            new DispatcherOneShotTimer(),
            new DispatcherOneShotTimer(),
            toUi);
        _window.PreviewKeyDown += OnIslandPreviewKeyDown;
        _converterGuards = new ConverterGuardLink(viewModel, _converter);
        _converterBusy = new ConverterBusyLink(viewModel, _converterQueue);
        // Settings: its own window from the tray, and every saved change takes effect at once (ADR-0016). The same Apply runs now for the saved values.
        // One Start with Windows switch shared by the Settings window and the tray, so both always show the same state (FE-26, BE-29).
        _startupSwitch = new StartupSwitch(_services.GetRequiredService<IStartupRegistration>(), _log);
        _settingsWindow = new SettingsWindowLauncher(
            _settingsService,
            _services.GetRequiredService<IWindowEnvironment>(),
            _services.GetRequiredService<IFolderPicker>(),
            toUi,
            _services.GetRequiredService<DiagnosticsRun>().Enabled,
            _log,
            startup: _startupSwitch,
            capture: _capture);
        _settingsApplier = new SettingsApplier(
            _settingsService,
            toUi,
            () => SystemParameters.ClientAreaAnimation,
            _theme,
            island => _position?.UpdatePreference(island),
            (island, off) => _toastPresenter?.Update(island, off),
            off =>
            {
                _shell?.SetReduceMotion(off);
                _transition?.SetReduceMotion(off);
            },
            off =>
            {
                if (_eyes is not null)
                    _eyes.ReduceMotion = off;
            },
            folder => _converter?.SetDefaultOutputFolder(folder));
        _settingsApplier.Apply(settings);
        _tray = new TrayController(_window.OpenForKeyboard, Shutdown, openSettings: _settingsWindow.Show, startup: _startupSwitch);
        _instance.ActivationRequested += OnActivationRequested;
        _log.Write(new DiagnosticEvent(DiagnosticLevel.Information, "app.start.primary", "App", "Start"));

        // The window is already on screen; the cards fill in as each reading arrives.
        // Working folders that a killed run left behind are removed now, off the UI thread, before any conversion of this run starts.
        await Task.WhenAll(
            _system.InitializeAsync(_lifetime.Token),
            _brightness.InitializeAsync(_lifetime.Token),
            _media.InitializeAsync(_lifetime.Token),
            _usage.InitializeAsync(_lifetime.Token),
            _notes.InitializeAsync(_lifetime.Token),
            RemoveLegacyUsageTokenAsync(),
            _services.GetRequiredService<ConversionOrchestrator>().RecoverAbandonedAsync());
    }

    // ADR-0015: a token pasted earlier (from "claude setup-token") can never read usage, so it is deleted once instead of staying on disk.
    private async Task RemoveLegacyUsageTokenAsync()
    {
        try
        {
            var store = _services?.GetRequiredService<IUsageTokenStore>();
            if (store is null)
                return;
            var has = await store.HasTokenAsync(_lifetime.Token);
            if (has.IsSuccess && has.Value && (await store.RemoveAsync(_lifetime.Token)).IsSuccess)
                _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "usage.legacy_token.removed", "App", "Start"));
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private void OnIslandPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && _resetToasts?.Dismiss() == true)
            e.Handled = true;
    }

    // Raised on a thread-pool thread by the coordinator; the window must be touched on the Dispatcher.
    private void OnActivationRequested()
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Debug, "app.activation.requested", "App", "Activate"));
        Dispatcher.BeginInvoke(() => _window?.OpenForKeyboard());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The open note is saved before anything is torn down. The save never comes back to this thread (the service and repository use
        // the thread pool), so waiting here cannot deadlock; it is bounded so a stuck disk never keeps Lunar from closing.
        try
        {
            _notes?.FlushForExitAsync().Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException ex)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "notes.exit.flush.failed", "App", "Exit", ErrorCode.Unexpected,
                ExceptionType: ex.InnerException?.GetType()));
        }

        _lifetime.Cancel();
        if (_instance is not null)
            _instance.ActivationRequested -= OnActivationRequested;
        _settingsApplier?.Dispose();
        _settingsApplier = null;
        _settingsWindow?.Dispose();
        _settingsWindow = null;
        _startupSwitch = null;
        _tray?.Dispose();
        _tray = null;
        _transition?.Dispose();
        _transition = null;
        _clockLifetime?.Dispose();
        _clockLifetime = null;
        _mediaLifetime?.Dispose();
        _mediaLifetime = null;
        _resetToasts?.Dispose();
        _resetToasts = null;
        _usageLifetime?.Dispose();
        _usageLifetime = null;
        _usage?.Dispose();
        _usage = null;
        _notesLifetime?.Dispose();
        _notesLifetime = null;
        _notes?.Dispose();
        _notes = null;
        _media?.Dispose();
        _media = null;
        _converterBusy?.Dispose();
        _converterBusy = null;
        _converterGuards?.Dispose();
        _converterGuards = null;
        _converterQueue?.Dispose();
        _converterQueue = null;
        _converter?.Dispose();
        _converter = null;
        _system?.Dispose();
        _system = null;
        _brightnessLifetime?.Dispose();
        _brightnessLifetime = null;
        _brightness?.Dispose();
        _brightness = null;
        _brightnessWriter?.Dispose();
        _brightnessWriter = null;
        _shell?.Dispose();
        _shell = null;
        _eyes?.Dispose();
        _eyes = null;
        _position?.Dispose();
        _position = null;
        _instance?.Dispose();
        _instance = null;
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "app.exit", "App", "Exit",
            Duration: _uptime.Elapsed));
        _theme?.Dispose();
        _theme = null;
        _capture?.Dispose(); // last: windows closing above stay hidden until they are gone
        _capture = null;
        _toastPresenter = null;
        _settingsService = null;
        _services?.Dispose();
        _services = null;
        _log = null;
        _lifetime.Dispose();
        base.OnExit(e);
    }
}
