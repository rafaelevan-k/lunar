namespace Lunar.Application.Shell.Models;

/// <summary>
/// One display in physical pixels. <see cref="Id"/> is the display device name (for example <c>\\.\DISPLAY1</c>),
/// which is not personal data and is the key a later setting may store. <see cref="WorkArea"/> excludes the taskbar.
/// </summary>
public sealed record DisplayInfo
{
    public const int BaselineDpi = 96;

    public DisplayInfo(string id, bool isPrimary, PixelRect bounds, PixelRect workArea, int dpiX, int dpiY)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(bounds);
        ArgumentNullException.ThrowIfNull(workArea);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dpiY);
        if (!bounds.Contains(workArea))
            throw new ArgumentException("The work area must lie inside the display bounds.", nameof(workArea));

        Id = id;
        IsPrimary = isPrimary;
        Bounds = bounds;
        WorkArea = workArea;
        DpiX = dpiX;
        DpiY = dpiY;
    }

    public string Id { get; }

    public bool IsPrimary { get; }

    public PixelRect Bounds { get; }

    public PixelRect WorkArea { get; }

    public int DpiX { get; }

    public int DpiY { get; }

    public double ScaleX => (double)DpiX / BaselineDpi;

    public double ScaleY => (double)DpiY / BaselineDpi;

    /// <summary>Used when no display can be read, so the shell always has a visible position.</summary>
    public static DisplayInfo Fallback { get; } = new(
        "fallback",
        true,
        new PixelRect(0, 0, 1280, 720),
        new PixelRect(0, 0, 1280, 720),
        BaselineDpi,
        BaselineDpi);
}
