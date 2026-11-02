using Lunar.Application.Shell.Models;
using Lunar.Domain.Shared;

namespace Lunar.Application.Shell;

/// <summary>
/// Pure placement rules (ADR-0010): which display to use and where the island goes on it. Works in physical
/// pixels; the caller converts to device-independent units only for WPF properties.
/// </summary>
public static class IslandPlacement
{
    /// <summary>The preferred display if present, otherwise the primary, otherwise the first.</summary>
    public static Result<DisplayInfo> SelectDisplay(IReadOnlyList<DisplayInfo> displays, string? preferredId)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (displays.Count == 0)
            return Result.Failure<DisplayInfo>(new OperationError(ErrorCode.DeviceUnavailable, "No display is available."));

        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            foreach (var display in displays)
            {
                if (string.Equals(display.Id, preferredId, StringComparison.OrdinalIgnoreCase))
                    return Result.Success(display);
            }
        }

        foreach (var display in displays)
        {
            if (display.IsPrimary)
                return Result.Success(display);
        }

        return Result.Success(displays[0]);
    }

    /// <summary>
    /// Centers a <paramref name="widthDip"/> × <paramref name="heightDip"/> island horizontally in the work area,
    /// <paramref name="topOffsetDip"/> below its top edge, converted with the display's scale and then clamped so
    /// it lies entirely inside the work area (shrinking only if it cannot fit).
    /// </summary>
    public static PixelRect PlaceTopCenter(DisplayInfo display, double widthDip, double heightDip, double topOffsetDip)
    {
        ArgumentNullException.ThrowIfNull(display);
        if (!double.IsFinite(widthDip) || widthDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(widthDip), widthDip, "Width must be a positive number.");
        if (!double.IsFinite(heightDip) || heightDip <= 0)
            throw new ArgumentOutOfRangeException(nameof(heightDip), heightDip, "Height must be a positive number.");
        if (!double.IsFinite(topOffsetDip) || topOffsetDip < 0)
            throw new ArgumentOutOfRangeException(nameof(topOffsetDip), topOffsetDip, "Offset must be zero or positive.");

        var area = display.WorkArea;
        var width = Math.Clamp((int)Math.Round(widthDip * display.ScaleX, MidpointRounding.AwayFromZero), 1, Math.Max(area.Width, 1));
        var height = Math.Clamp((int)Math.Round(heightDip * display.ScaleY, MidpointRounding.AwayFromZero), 1, Math.Max(area.Height, 1));
        var offset = (int)Math.Round(topOffsetDip * display.ScaleY, MidpointRounding.AwayFromZero);

        var x = area.X + (area.Width - width) / 2;
        var y = area.Y + offset;

        x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - width));
        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        return new PixelRect(x, y, width, height);
    }
}
