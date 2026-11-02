namespace Lunar.Application.Media.Models;

/// <summary>Operations the session itself reports as available. Absent support must not be faked.</summary>
public sealed record MediaCapabilities(bool CanPlayPause, bool CanNext, bool CanPrevious, bool CanSeek)
{
    public static MediaCapabilities None { get; } = new(false, false, false, false);
}
