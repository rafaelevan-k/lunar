using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings;
using Lunar.Desktop.Features.Converter;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Settings;

/// <summary>
/// Opens the Settings window, never more than one: asking again brings the open one forward. The window and its view model live only while
/// it is open; closing it releases both. Use on the UI thread.
/// </summary>
internal sealed class SettingsWindowLauncher : IDisposable
{
    private readonly SettingsService _settings;
    private readonly IWindowEnvironment _environment;
    private readonly IFolderPicker _folders;
    private readonly Action<Action> _post;
    private readonly bool _diagnosticsInUse;
    private readonly IDiagnosticLog? _log;
    private readonly bool _interactive;
    private readonly StartupSwitch? _startup;
    private readonly CaptureExclusionGuard? _capture;
    private readonly Lunar.Desktop.Features.Updates.UpdatePromptViewModel? _updates;
    private SettingsWindow? _window;
    private SettingsViewModel? _model;
    private bool _disposed;

    internal SettingsWindowLauncher(
        SettingsService settings,
        IWindowEnvironment environment,
        IFolderPicker folders,
        Action<Action> post,
        bool diagnosticsInUse,
        IDiagnosticLog? log = null,
        bool interactive = true,
        StartupSwitch? startup = null,
        CaptureExclusionGuard? capture = null,
        Lunar.Desktop.Features.Updates.UpdatePromptViewModel? updates = null)
    {
        _settings = settings;
        _environment = environment;
        _folders = folders;
        _post = post;
        _diagnosticsInUse = diagnosticsInUse;
        _log = log;
        _interactive = interactive;
        _startup = startup;
        _capture = capture;
        _updates = updates;
    }

    internal bool IsOpen => _window is not null;

    internal SettingsWindow? Window => _window;

    internal void Show()
    {
        if (_disposed)
            return;

        if (_window is { } open)
        {
            if (open.WindowState == System.Windows.WindowState.Minimized)
                open.WindowState = System.Windows.WindowState.Normal;
            if (_interactive)
                open.Activate();
            return;
        }

        _model = new SettingsViewModel(_settings, _environment, _folders, _post, _diagnosticsInUse, _log, _startup, _capture, _updates);
        _window = new SettingsWindow { DataContext = _model };
        _capture?.Attach(_window); // hidden from screen sharing before it is shown (ADR-0019)
        _window.Closed += OnClosed;
        if (!_interactive)
        {
            // Tests only: far off screen, not in the taskbar, never taking focus from whatever the user is doing.
            _window.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            _window.Left = -20000;
            _window.Top = -20000;
            _window.ShowActivated = false;
            _window.ShowInTaskbar = false;
            _window.Show();
            return;
        }

        _window.Show();
        _window.Activate();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _window?.Close();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow window)
            window.Closed -= OnClosed;
        _model?.Dispose();
        _model = null;
        _window = null;
    }
}
