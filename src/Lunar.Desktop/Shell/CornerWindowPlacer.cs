using System.Windows;
using System.Windows.Media;
using Lunar.Application.Abstractions.Shell;
using Lunar.Application.Settings.Models;
using Lunar.Application.Shell;
using Lunar.Application.Shell.Models;
using Lunar.Desktop.Features.Usage;
using Lunar.Domain.Shared;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Puts a small pop-up window in the bottom-right corner of the work area of the display the island is on (the preferred display if set, else the
/// primary), at its measured height and the display's own scale. Shared by the Usage reset pop-up (FE-28) and the new-version prompt (FE-38).
/// UI thread only.
/// </summary>
internal static class CornerWindowPlacer
{
    public const double MarginDip = 12;

    /// <param name="fallback">Told when the display could not be read and the fallback display was used.</param>
    public static void Place(Window window, IWindowEnvironment environment, IslandSettings preference, Action<ErrorCode>? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(preference);
        var display = ChooseDisplay(environment, preference, fallback);
        var height = window.IsLoaded && window.ActualHeight > 0 ? window.ActualHeight : MeasureHeight(window);
        var rect = ResetToastPlacement.BottomRight(display, window.Width, height, MarginDip);
        var dpi = VisualTreeHelper.GetDpi(window);
        var (left, top) = IslandPositionController.ToWindowOrigin(rect, dpi.DpiScaleX, dpi.DpiScaleY);
        if (window.Left != left)
            window.Left = left;
        if (window.Top != top)
            window.Top = top;
    }

    // Before the window is shown its height is what its card needs at the fixed width.
    private static double MeasureHeight(Window window)
    {
        if (window.Content is not FrameworkElement card)
            return 64;
        card.Measure(new Size(window.Width, double.PositiveInfinity));
        return card.DesiredSize.Height > 0 ? card.DesiredSize.Height : 64;
    }

    private static DisplayInfo ChooseDisplay(IWindowEnvironment environment, IslandSettings preference, Action<ErrorCode>? fallback)
    {
        var displays = environment.GetDisplays();
        if (displays.IsFailure)
        {
            fallback?.Invoke(displays.Error!.Code);
            return DisplayInfo.Fallback;
        }

        var selected = IslandPlacement.SelectDisplay(displays.Value, preference.PreferredMonitorId);
        if (selected.IsFailure)
        {
            fallback?.Invoke(selected.Error!.Code);
            return DisplayInfo.Fallback;
        }

        return selected.Value;
    }
}
