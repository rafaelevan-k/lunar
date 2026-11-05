using System.Windows.Interop;
using System.Windows.Media;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell;
using Lunar.Application.Shell.Models;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Sizes and places the island on the preferred display's work area (ADR-0010, FE-03, FE-05). The size is the
/// measured content clamped into <see cref="IslandLayout"/>'s limits for the current layout, so it fits the work
/// area at every scale; the window is then centred at the top. Both are redone whenever Windows reports a display,
/// taskbar or DPI change, the layout changes, or the content is replaced. It falls back to the primary display when
/// the preferred one is gone, and to a safe 1280×720 display when none can be read, so the island is never off
/// screen. It uses only WPF window properties and the <see cref="IWindowEnvironment"/> port.
/// </summary>
public sealed class IslandPositionController : IDisposable
{
    private const string Component = "IslandPosition";

    // Added to the physical coordinate before converting to device-independent units so rounding or truncation
    // inside WPF still lands on the intended physical pixel.
    private const double PixelNudge = 0.25;

    private readonly IslandWindow _window;
    private readonly IslandViewModel _viewModel;
    private readonly IWindowEnvironment _environment;
    private readonly IDiagnosticLog _log;
    private IslandSettings _preference;
    private bool _started;
    private bool _disposed;

    public IslandPositionController(
        IslandWindow window,
        IslandViewModel viewModel,
        IWindowEnvironment environment,
        IDiagnosticLog log,
        IslandSettings preference)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(preference);
        _window = window;
        _viewModel = viewModel;
        _environment = environment;
        _log = log;
        _preference = preference;
    }

    /// <summary>
    /// Places the window and starts following display changes. Call on the UI thread before showing the window.
    /// A listener failure is logged and returned, but the window is still placed once.
    /// </summary>
    public Result Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started)
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "Positioning was already started."));
        _started = true;

        // The window handle fixes the window's DPI, which the conversion to WPF units needs.
        _ = new WindowInteropHelper(_window).EnsureHandle();

        _environment.DisplaysChanged += OnDisplaysChanged;
        _window.DpiChanged += OnWindowDpiChanged;
        _window.ViewContentChanged += OnLayoutInputChanged;
        _viewModel.PropertyChanged += OnViewModelChanged;
        var listener = _environment.Start();
        if (listener.IsFailure)
            Log(DiagnosticLevel.Warning, "island.position.listener.failed", "Start", listener.Error!.Code);

        Apply();
        return listener;
    }

    /// <summary>The user changed the display or offset setting (ADR-0016): the island moves at once. UI thread only.</summary>
    internal void UpdatePreference(IslandSettings preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        if (_disposed || preference == _preference)
            return;
        _preference = preference;
        if (_started)
            Apply();
    }

    /// <summary>Re-reads the displays and moves the window. Safe to call repeatedly; must run on the UI thread.</summary>
    internal void Apply()
    {
        if (_disposed)
            return;

        var display = ChooseDisplay();
        var limits = IslandLayout.LimitsFor(_viewModel.IsExpandedLayout, display, _preference.TopOffsetDip);
        var content = _window.MeasureContent(new System.Windows.Size(limits.MaxWidth, limits.MaxHeight));
        var (width, height) = IslandLayout.Fit(limits, content.Width, content.Height);
        _window.ApplySize(limits, width, height);
        var rect = IslandPlacement.PlaceTopCenter(display, width, height, _preference.TopOffsetDip);

        var dpi = VisualTreeHelper.GetDpi(_window);
        var (left, top) = ToWindowOrigin(rect, dpi.DpiScaleX, dpi.DpiScaleY);
        if (_window.Left != left)
            _window.Left = left;
        if (_window.Top != top)
            _window.Top = top;
        Log(DiagnosticLevel.Debug, "island.position.applied", "Apply");
    }

    /// <summary>
    /// WPF positions a window in device-independent units of the monitor it is currently on, so the physical
    /// target is divided by that monitor's scale.
    /// </summary>
    internal static (double Left, double Top) ToWindowOrigin(PixelRect rect, double scaleX, double scaleY)
    {
        ArgumentNullException.ThrowIfNull(rect);
        if (!double.IsFinite(scaleX) || scaleX <= 0 || !double.IsFinite(scaleY) || scaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleX), "Scale must be a positive number.");

        return (Nudge(rect.X) / scaleX, Nudge(rect.Y) / scaleY);
    }

    // Away from zero, so truncation toward zero and rounding both give back the same pixel (including negative
    // coordinates on a monitor left of or above the primary one).
    private static double Nudge(int pixel) => pixel >= 0 ? pixel + PixelNudge : pixel - PixelNudge;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _environment.DisplaysChanged -= OnDisplaysChanged;
        _window.DpiChanged -= OnWindowDpiChanged;
        _window.ViewContentChanged -= OnLayoutInputChanged;
        _viewModel.PropertyChanged -= OnViewModelChanged;
    }

    private DisplayInfo ChooseDisplay()
    {
        var displays = _environment.GetDisplays();
        if (displays.IsFailure)
        {
            Log(DiagnosticLevel.Warning, "island.position.fallback", "Apply", displays.Error!.Code);
            return DisplayInfo.Fallback;
        }

        var selected = IslandPlacement.SelectDisplay(displays.Value, _preference.PreferredMonitorId);
        if (selected.IsFailure)
        {
            Log(DiagnosticLevel.Warning, "island.position.fallback", "Apply", selected.Error!.Code);
            return DisplayInfo.Fallback;
        }

        return selected.Value;
    }

    // Raised on a thread-pool thread (ADR-0010); the window is touched only on its dispatcher.
    private void OnDisplaysChanged()
    {
        var dispatcher = _window.Dispatcher;
        if (_disposed || dispatcher.HasShutdownStarted)
            return;

        Log(DiagnosticLevel.Debug, "island.display.changed", "Notify");
        dispatcher.BeginInvoke(Apply);
    }

    // WM_DPICHANGED may not reach the hidden listener window, so WPF's own notification is a second trigger.
    private void OnWindowDpiChanged(object sender, System.Windows.DpiChangedEventArgs e)
    {
        Log(DiagnosticLevel.Debug, "island.window.dpi.changed", "Notify");
        Apply();
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IslandViewModel.IsExpandedLayout))
            Apply();
    }

    private void OnLayoutInputChanged(object? sender, EventArgs e) => Apply();

    private void Log(DiagnosticLevel level, string eventId, string operation, ErrorCode? error = null) =>
        _log.Write(new DiagnosticEvent(level, eventId, Component, operation, error));
}
