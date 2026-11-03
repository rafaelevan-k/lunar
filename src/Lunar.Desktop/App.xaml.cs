using System.Diagnostics;
using System.Windows;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Settings;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Composition;
using Lunar.Desktop.Features.Eyes;
using Lunar.Desktop.Shell;
using Lunar.Domain.Shared;
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

    // async void is limited to this framework override and every path is guarded.
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

    private async Task StartAsync()
    {
        if (SystemParameters.HighContrast)
            Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri("Themes/Colors.HighContrast.xaml", UriKind.Relative) };

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
        var loaded = await _services.GetRequiredService<ISettingsRepository>().LoadAsync(_lifetime.Token);
        if (_services is null)
            return; // shut down while the file was being read
        var settings = loaded.IsSuccess ? loaded.Value.Settings : LunarSettings.Defaults;

        var viewModel = _services.GetRequiredService<IslandViewModel>();
        var eyesView = new EyesView();
        // Motion is off when the Lunar setting says so, when idle eye animation is disabled, or when Windows has
        // "Show animations" turned off. Read once at startup.
        var motionOff = settings.Appearance.ReduceMotion || !settings.Appearance.EyeAnimationEnabled || !SystemParameters.ClientAreaAnimation;
        _eyes = new EyeAnimationController(new DispatcherEyeTimer(), SystemRandomSource.Instance, TimeProvider.System)
        {
            ReduceMotion = motionOff
        };
        eyesView.Attach(_eyes);
        _eyes.Start();
        // Clicking the eyes toggles the island; the state machine decides and IslandTransitionController animates it.
        eyesView.ClickCommand = viewModel.ToggleCommand;
        _window = new IslandWindow { DataContext = viewModel, ViewContent = eyesView };
        MainWindow = _window;
        _position = new IslandPositionController(_window, viewModel, _services.GetRequiredService<IWindowEnvironment>(), _log, settings.Island);
        _transition = new IslandTransitionController(_window, viewModel, motionOff);
        _position.Start(); // placed before it is shown; a listener failure is logged inside and the window stays placed
        _window.Show();

        _tray = new TrayController(_window.OpenForKeyboard, Shutdown);
        _instance.ActivationRequested += OnActivationRequested;
        _log.Write(new DiagnosticEvent(DiagnosticLevel.Information, "app.start.primary", "App", "Start"));
    }

    // Raised on a thread-pool thread by the coordinator; the window must be touched on the Dispatcher.
    private void OnActivationRequested()
    {
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Debug, "app.activation.requested", "App", "Activate"));
        Dispatcher.BeginInvoke(() => _window?.OpenForKeyboard());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _lifetime.Cancel();
        if (_instance is not null)
            _instance.ActivationRequested -= OnActivationRequested;
        _tray?.Dispose();
        _tray = null;
        _transition?.Dispose();
        _transition = null;
        _eyes?.Dispose();
        _eyes = null;
        _position?.Dispose();
        _position = null;
        _instance?.Dispose();
        _instance = null;
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Information, "app.exit", "App", "Exit",
            Duration: _uptime.Elapsed));
        _services?.Dispose();
        _services = null;
        _log = null;
        _lifetime.Dispose();
        base.OnExit(e);
    }
}
