using Lunar.Application.Settings.Models;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Puts one island style into effect everywhere it matters (ADR-0027), in the order that keeps sizes consistent: the view model (the collapsed pill's
/// height and width), the shell (the strip's height, the shoulders' inset, the tabs), the window (outline, border, palette) and last the position
/// (the Notch at the top edge, measured with the new border). UI thread only; the island must be collapsed.
/// </summary>
internal static class IslandStyleApplier
{
    public static void Apply(IslandStyle style, IslandViewModel island, IslandShellView shell, IslandWindow window, IslandPositionController position, bool place = true)
    {
        ArgumentNullException.ThrowIfNull(island);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(position);
        island.Style = style;
        shell.SetStyle(style, island.IdleHeight, IslandOutline.Border(style));
        window.ApplyStyle(style);
        position.UpdateStyle(style);
        if (place)
            position.Apply();
    }
}
