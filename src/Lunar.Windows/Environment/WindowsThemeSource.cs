using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Domain.Shared;
using Lunar.Windows.Interop;
using Microsoft.Win32;

namespace Lunar.Windows.Environment;

/// <summary>
/// The Windows light or dark app theme (ADR-0016): one read-only registry value of the current user and a change
/// notification on its key. Nothing is written. A value that cannot be read means dark, which is Lunar's own look. The
/// watcher is one background thread that exists only while somebody listens; it ends when the last listener leaves or the
/// source is disposed.
/// </summary>
public sealed class WindowsThemeSource : ISystemThemeSource, IDisposable
{
    internal const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    internal const string ValueName = "AppsUseLightTheme";

    private const string Component = "SystemTheme";

    private readonly Func<int?> _read;
    private readonly string _keyPath;
    private readonly IDiagnosticLog? _log;
    private readonly object _gate = new();
    private Action? _changed;
    private Watcher? _watcher;
    private bool _disposed;
    private bool _readFailureLogged;

    public WindowsThemeSource(IDiagnosticLog? log = null)
        : this(() => ReadFromRegistry(KeyPath), KeyPath, log)
    {
    }

    internal WindowsThemeSource(Func<int?> read, string keyPath, IDiagnosticLog? log = null)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _keyPath = keyPath ?? throw new ArgumentNullException(nameof(keyPath));
        _log = log;
    }

    public event Action? Changed
    {
        add
        {
            lock (_gate)
            {
                if (_disposed)
                    return;
                _changed += value;
                _watcher ??= Watcher.TryStart(_keyPath, OnKeyChanged, _log);
            }
        }

        remove
        {
            Watcher? stop = null;
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    stop = _watcher;
                    _watcher = null;
                }
            }

            stop?.Dispose();
        }
    }

    public bool PrefersLight
    {
        get
        {
            try
            {
                return _read() == 1;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                if (!_readFailureLogged)
                {
                    _readFailureLogged = true;
                    _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "theme.read.failed", Component, "Read", ErrorCode.AccessDenied, ExceptionType: ex.GetType()));
                }

                return false;
            }
        }
    }

    public void Dispose()
    {
        Watcher? stop;
        lock (_gate)
        {
            _disposed = true;
            _changed = null;
            stop = _watcher;
            _watcher = null;
        }

        stop?.Dispose();
    }

    private static int? ReadFromRegistry(string keyPath)
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
        return key?.GetValue(ValueName) as int?;
    }

    private void OnKeyChanged()
    {
        Action? handler;
        lock (_gate)
            handler = _changed;
        handler?.Invoke();
    }

    /// <summary>Waits for the personalisation key to change; one thread, one auto-reset event, one stop event.</summary>
    private sealed class Watcher : IDisposable
    {
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly Thread _thread;
        private readonly Action _onChange;
        private readonly string _keyPath;

        private Watcher(string keyPath, Action onChange)
        {
            _onChange = onChange;
            _keyPath = keyPath;
            _thread = new Thread(Run) { IsBackground = true, Name = "Lunar.ThemeWatcher" };
        }

        public static Watcher? TryStart(string keyPath, Action onChange, IDiagnosticLog? log)
        {
            try
            {
                var watcher = new Watcher(keyPath, onChange);
                watcher._thread.Start();
                return watcher;
            }
            catch (Exception ex) when (ex is OutOfMemoryException or ThreadStartException)
            {
                log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "theme.watch.failed", Component, "Watch", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
                return null;
            }
        }

        public void Dispose()
        {
            _stop.Set();
            if (Thread.CurrentThread != _thread)
                _thread.Join(TimeSpan.FromSeconds(2));
            _stop.Dispose();
        }

        private void Run()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: false);
                if (key is null)
                    return;
                using var changed = new AutoResetEvent(false);
                while (!_stop.IsSet)
                {
                    var status = NativeMethods.RegNotifyChangeKeyValue(key.Handle, false, NativeMethods.REG_NOTIFY_CHANGE_LAST_SET, changed.SafeWaitHandle, true);
                    if (status != 0)
                        return;
                    var index = WaitHandle.WaitAny([changed, _stop.WaitHandle]);
                    if (index != 0)
                        return;
                    _onChange();
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // The key or the event went away while shutting down, or access was refused: the theme then simply stays as it is.
            }
        }
    }
}
