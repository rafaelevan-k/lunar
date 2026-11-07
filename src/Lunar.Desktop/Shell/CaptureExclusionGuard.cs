using System.Windows;
using System.Windows.Interop;
using Lunar.Application.Abstractions.Shell;

namespace Lunar.Desktop.Shell;

/// <summary>
/// Keeps every Lunar window out of screen sharing, screenshots and recordings (ADR-0019, FE-30). Always on: there is nothing to turn it off.
/// <list type="bullet">
/// <item><see cref="Attach"/> excludes one of Lunar's own windows (the island, the reset pop-up, the Settings window) as soon as it has a
/// handle, before it is first shown, and once per handle.</item>
/// <item><see cref="StartWatching"/> covers the windows Lunar does not build itself: tooltips, the tray menu, Windows' file and folder
/// pickers and message boxes.</item>
/// </list>
/// <see cref="IsRefused"/> says truthfully when Windows refused any of it, so the Settings window can tell the user. UI thread only.
/// </summary>
public sealed class CaptureExclusionGuard : IDisposable
{
    private readonly ICaptureExclusion _exclusion;
    private readonly HashSet<nint> _excluded = [];
    private IDisposable? _watch;
    private bool _disposed;

    internal CaptureExclusionGuard(ICaptureExclusion exclusion)
    {
        ArgumentNullException.ThrowIfNull(exclusion);
        _exclusion = exclusion;
    }

    /// <summary>True once Windows has refused to hide any Lunar window, or the watcher could not start.</summary>
    internal bool IsRefused { get; private set; }

    /// <summary>Raised when <see cref="IsRefused"/> becomes true.</summary>
    internal event Action? Changed;

    /// <summary>Starts the process-wide watcher. Call once, before any window is created.</summary>
    internal void StartWatching()
    {
        if (_disposed || _watch is not null)
            return;
        var watch = _exclusion.ExcludeEveryNewWindow();
        if (watch.IsSuccess)
            _watch = watch.Value;
        else
            Refused();
    }

    /// <summary>Excludes the window now if it has a handle, otherwise the moment it gets one (before it is shown).</summary>
    internal void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_disposed)
            return;

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != 0)
            Apply(window, handle);
        else
            window.SourceInitialized += OnSourceInitialized;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _watch?.Dispose();
        _watch = null;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.SourceInitialized -= OnSourceInitialized;
        Apply(window, new WindowInteropHelper(window).Handle);
    }

    private void Apply(Window window, nint handle)
    {
        if (!_excluded.Add(handle))
            return; // once per handle

        // Windows may hand the same number to a later window once this one is gone.
        void OnClosed(object? sender, EventArgs e)
        {
            window.Closed -= OnClosed;
            _excluded.Remove(handle);
        }

        window.Closed += OnClosed;
        if (_exclusion.Exclude(handle).IsFailure)
            Refused(); // the adapter has logged the code
    }

    private void Refused()
    {
        if (IsRefused)
            return;
        IsRefused = true;
        Changed?.Invoke();
    }
}
