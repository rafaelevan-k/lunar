using System.Runtime.InteropServices;

namespace Lunar.Desktop.Shell;

/// <summary>
/// The only native call in Desktop: sets WS_EX_NOACTIVATE so clicking the pill never changes the foreground
/// window. WPF has no property for it, and returning MA_NOACTIVATE from WM_MOUSEACTIVATE alone was observed to
/// leave no foreground window after a click. Narrow window-mechanics exception recorded in ADR-0007.
/// </summary>
internal static class NonActivatingWindowStyle
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000L;

    internal static void Apply(IntPtr hwnd) => Set(hwnd, enabled: true);

    /// <summary>Lets keyboard-driven activation (tray Open, FE-06) take focus; call <see cref="Apply"/> again afterwards.</summary>
    internal static void Remove(IntPtr hwnd) => Set(hwnd, enabled: false);

    internal static bool IsApplied(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_NOACTIVATE) != 0;

    private static void Set(IntPtr hwnd, bool enabled)
    {
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        style = enabled ? style | WS_EX_NOACTIVATE : style & ~WS_EX_NOACTIVATE;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style));
    }

    // DllImport rather than LibraryImport: the generator would require AllowUnsafeBlocks for the whole project.
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
}
