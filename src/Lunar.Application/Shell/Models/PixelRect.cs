namespace Lunar.Application.Shell.Models;

/// <summary>Rectangle in physical screen pixels; may have negative coordinates on monitors left of or above the primary.</summary>
public sealed record PixelRect
{
    public PixelRect(int x, int y, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(width);
        ArgumentOutOfRangeException.ThrowIfNegative(height);
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public int X { get; }

    public int Y { get; }

    public int Width { get; }

    public int Height { get; }

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool Contains(PixelRect other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
    }
}
