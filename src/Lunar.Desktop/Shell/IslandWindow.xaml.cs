using System.Windows;
using System.Windows.Interop;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Borderless transparent topmost window that hosts the island pill. Window mechanics only: it never takes
/// activation from the app the user is working in, except while the user explicitly opens it for keyboard use.
/// Business logic and OS calls do not belong here.
/// </summary>
public partial class IslandWindow : Window
{
    public IslandWindow()
    {
        InitializeComponent();

        // Size limits and placement belong to IslandPositionController (FE-03, FE-05); this window only applies them.
        SourceInitialized += OnSourceInitialized;
        Deactivated += OnDeactivated;
        Closed += OnClosed;
    }

    /// <summary>Raised after <see cref="ViewContent"/> is replaced, so the owner can measure the new content.</summary>
    public event EventHandler? ViewContentChanged;

    /// <summary>The single place later features present their content.</summary>
    public object? ViewContent
    {
        get => ViewHost.Content;
        set
        {
            ViewHost.Content = value;
            ViewContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The size, in device-independent units, the pill wants for its current content within the available space.</summary>
    internal Size MeasureContent(Size available)
    {
        // WPF reuses a cached measurement for an element whose own layout is unchanged, so a change deep inside
        // the content would be missed. The tree is small, so every element is invalidated to force a real measure.
        InvalidateMeasureDeep(Pill);
        Pill.Measure(available);
        return Pill.DesiredSize;
    }

    private static void InvalidateMeasureDeep(DependencyObject node)
    {
        if (node is UIElement element)
            element.InvalidateMeasure();
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++)
            InvalidateMeasureDeep(System.Windows.Media.VisualTreeHelper.GetChild(node, i));
    }

    /// <summary>Applies the allowed range and the chosen size in device-independent units.</summary>
    internal void ApplySize(IslandLayoutLimits limits, double width, double height)
    {
        MinWidth = limits.MinWidth;
        MinHeight = limits.MinHeight;
        MaxWidth = limits.MaxWidth;
        MaxHeight = limits.MaxHeight;
        if (Width != width)
            Width = width;
        if (Height != height)
            Height = height;
    }

    /// <summary>
    /// Explicit keyboard/tray activation (ADR-0007): temporarily allows activation, takes focus, and goes back
    /// to non-activating as soon as the window loses focus.
    /// </summary>
    public void OpenForKeyboard()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        NonActivatingWindowStyle.Remove(handle);
        Show();
        Activate();
        Focus();
    }

    private void OnSourceInitialized(object? sender, EventArgs e) =>
        NonActivatingWindowStyle.Apply(new WindowInteropHelper(this).Handle);

    private void OnDeactivated(object? sender, EventArgs e) =>
        NonActivatingWindowStyle.Apply(new WindowInteropHelper(this).Handle);

    private void OnClosed(object? sender, EventArgs e)
    {
        SourceInitialized -= OnSourceInitialized;
        Deactivated -= OnDeactivated;
        Closed -= OnClosed;
    }
}
