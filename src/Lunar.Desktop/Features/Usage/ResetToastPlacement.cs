using Lunar.Application.Shell.Models;

namespace Lunar.Desktop.Features.Usage;

/// <summary>
/// Where the reset pop-up goes (FE-28): the bottom-right corner of the work area of the display, so it never covers the taskbar,
/// <paramref name="marginDip"/> in from the edges, converted with the display's own scale and kept entirely inside the work area. Works in
/// physical pixels like <c>IslandPlacement</c>; the caller converts to window units.
/// </summary>
internal static class ResetToastPlacement
{
    public static PixelRect BottomRight(DisplayInfo display, double widthDip, double heightDip, double marginDip)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!double.IsFinite(widthDip) || widthDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthDip), widthDip, "Width must be a positive number.");
        if (!double.IsFinite(heightDip) || heightDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(heightDip), heightDip, "Height must be a positive number.");
        if (!double.IsFinite(marginDip) || marginDip < 0)
            throw new ArgumentOutOfRangeException(nameof(marginDip), marginDip, "Margin must be zero or positive.");

        var area = display.WorkArea;
        var width = Math.Clamp((int)Math.Round(widthDip * display.ScaleX, MidpointRounding.AwayFromZero), 1, Math.Max(area.Width, 1));
        var height = Math.Clamp((int)Math.Round(heightDip * display.ScaleY, MidpointRounding.AwayFromZero), 1, Math.Max(area.Height, 1));
        var marginX = (int)Math.Round(marginDip * display.ScaleX, MidpointRounding.AwayFromZero);
        var marginY = (int)Math.Round(marginDip * display.ScaleY, MidpointRounding.AwayFromZero);

        var x = Math.Clamp(area.Right - width - marginX, area.X, Math.Max(area.X, area.Right - width));
        var y = Math.Clamp(area.Bottom - height - marginY, area.Y, Math.Max(area.Y, area.Bottom - height));
        return new PixelRect(x, y, width, height);
    }
}
