using System.Windows;
using System.Windows.Media.Animation;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// The real pop-up (FE-28): creates <see cref="ResetToastWindow"/> when there is something to say, puts it in the bottom-right corner of the
/// work area of the display the island is on (the preferred display if set, else the primary, as the island does), fades it in unless motion is
/// reduced, and closes it. The window is created and destroyed per pop-up, so nothing is held while idle. Must be used on the UI thread.
/// </summary>
internal sealed class ResetToastPresenter : IResetToastPresenter
{
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(150);

    private readonly IWindowEnvironment _environment;
    private IslandSettings _preference;
    private bool _reduceMotion;
    private readonly IDiagnosticLog? _log;
    private readonly CaptureExclusionGuard? _capture;
    private ResetToastWindow? _window;
    private bool _disposed;

    /// <param name="capture">Hides each pop-up from screen sharing before it is shown (ADR-0019); null only in tests.</param>
    public ResetToastPresenter(IWindowEnvironment environment, IslandSettings preference, bool reduceMotion, IDiagnosticLog? log = null,
        CaptureExclusionGuard? capture = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(preference);
        _environment = environment;
        _preference = preference;
        _reduceMotion = reduceMotion;
        _log = log;
        _capture = capture;
    }

    /// <summary>The user changed the display or the motion setting (ADR-0016); the next pop-up uses them. UI thread only.</summary>
    internal void Update(IslandSettings preference, bool reduceMotion)
    {
        ArgumentNullException.ThrowIfNull(preference);
        _preference = preference;
        _reduceMotion = reduceMotion;
    }

    public event Action? CloseRequested;

    public event Action? PointerEntered;

    public event Action? PointerLeft;

    /// <summary>The window on screen, or null; for tests.</summary>
    internal ResetToastWindow? Window => _window;

    public void Show(ResetToastContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (_disposed)
            return;

        var window = _window;
        var isNew = window is null;
        if (window is null)
        {
            window = new ResetToastWindow();
            window.CloseRequested += OnCloseRequested;
            window.PointerEntered += OnPointerEntered;
            window.PointerLeft += OnPointerLeft;
            window.DpiChanged += OnDpiChanged;
            _capture?.Attach(window);
            _window = window;
        }

        window.DataContext = content;
        if (!isNew)
            window.UpdateLayout(); // the new words may need another height
        // The window handle fixes its DPI, which the conversion to window units needs.
        _ = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
        Place(window);
        if (isNew)
        {
            window.Opacity = _reduceMotion ? 1 : 0;
            window.Show();
            if (!_reduceMotion)
                window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FadeIn) { FillBehavior = FillBehavior.HoldEnd });
            window.UpdateLayout();
            Place(window); // now the real height is known
        }

        window.Announce(content.AutomationName);
    }

    public void Hide()
    {
        var window = _window;
        if (window is null)
            return;
        _window = null;
        window.CloseRequested -= OnCloseRequested;
        window.PointerEntered -= OnPointerEntered;
        window.PointerLeft -= OnPointerLeft;
        window.DpiChanged -= OnDpiChanged;
        window.BeginAnimation(UIElement.OpacityProperty, null);
        window.Close();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Hide();
        _disposed = true;
    }

    private void Place(ResetToastWindow window) =>
        CornerWindowPlacer.Place(window, _environment, _preference, code => Log("usage.toast.display.fallback", code));

    // A window moved onto a monitor with another scale is placed again with that monitor's scale.
    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        if (_window is { } window)
            Place(window);
    }

    private void OnCloseRequested() => CloseRequested?.Invoke();

    private void OnPointerEntered() => PointerEntered?.Invoke();

    private void OnPointerLeft() => PointerLeft?.Invoke();

    private void Log(string eventId, Lunar.Domain.Shared.ErrorCode code) =>
        _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, eventId, "UsageToast", "Place", code));
}
