using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Updates;

/// <summary>
/// Shows the new-version prompt (FE-38) while <see cref="UpdatePromptViewModel.IsPromptVisible"/> is true: a <see cref="UpdatePromptWindow"/> in the
/// bottom-right corner of the island's display, faded in unless motion is reduced, hidden from screen sharing, and closed when it is no longer
/// wanted. The window exists only while shown. A screen reader hears the prompt when it appears and when its state changes, never the percent.
/// UI thread only.
/// </summary>
internal sealed class UpdatePromptPresenter : IDisposable
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(150);

    private readonly UpdatePromptViewModel _model;
    private readonly IWindowEnvironment _environment;
    private readonly IDiagnosticLog? _log;
    private readonly CaptureExclusionGuard? _capture;
    private IslandSettings _preference;
    private bool _reduceMotion;
    private UpdatePromptWindow? _window;
    private string? _lastAnnounced;
    private bool _disposed;

    public UpdatePromptPresenter(UpdatePromptViewModel model, IWindowEnvironment environment, IslandSettings preference, bool reduceMotion,
        IDiagnosticLog? log = null, CaptureExclusionGuard? capture = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(preference);
        _model = model;
        _environment = environment;
        _preference = preference;
        _reduceMotion = reduceMotion;
        _log = log;
        _capture = capture;
        _model.PropertyChanged += OnModelChanged;
        Sync();
    }

    /// <summary>The window on screen, or null; for tests.</summary>
    internal UpdatePromptWindow? Window => _window;

    /// <summary>The user changed the display or the motion setting; the prompt follows at once. UI thread only.</summary>
    internal void Update(IslandSettings preference, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(preference);
        _preference = preference;
        _reduceMotion = reduceMotion;
        if (_window is { } window)
            Place(window);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _model.PropertyChanged -= OnModelChanged;
        Hide();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(UpdatePromptViewModel.IsPromptVisible) or nameof(UpdatePromptViewModel.AnnouncementText) or nameof(UpdatePromptViewModel.Message))
            Sync();
    }

    private void Sync()
    {
        if (_disposed)
            return;
        if (!_model.IsPromptVisible)
        {
            Hide();
            return;
        }

        var window = _window;
        if (window is null)
        {
            window = new UpdatePromptWindow { DataContext = _model };
            window.DpiChanged += OnDpiChanged;
            _capture?.Attach(window);
            _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            Place(window);
            window.Opacity = _reduceMotion ? 1 : 0;
            window.Show();
            if (!_reduceMotion)
                window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FadeIn) { FillBehavior = FillBehavior.HoldEnd });
            _window = window;
        }

        window.UpdateLayout(); // the words or buttons may need another height
        Place(window);
        var words = _model.AnnouncementText;
        if (!string.Equals(words, _lastAnnounced, StringComparison.Ordinal))
        {
            _lastAnnounced = words;
            window.Announce(words);
        }
    }

    private void Hide()
    {
        var window = _window;
        _lastAnnounced = null;
        if (window is null)
            return;
        _window = null;
        window.DpiChanged -= OnDpiChanged;
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Close();
    }

    private void Place(UpdatePromptWindow window) =>
        CornerWindowPlacer.Place(window, _environment, _preference, code =>
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "update.prompt.display.fallback", "UpdatePrompt", "Place", code)));

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (_window is { } window)
            Place(window);
    }
}
