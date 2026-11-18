using System.Runtime.InteropServices;
using Lunar.Application.Abstractions.Diagnostics;
using Lunar.Application.Abstractions.System;
using Lunar.Application.Translate;
using Lunar.Domain.Shared;

namespace Lunar.Windows.Shell;

/// <summary>
/// <see cref="IGlobalShortcut"/> with <c>RegisterHotKey</c> (ADR-0028). The shortcut belongs to one background thread with its own message queue
/// (a hot key registered without a window is posted to the registering thread), which lives only while a shortcut is registered. Key repeat is off,
/// so holding the keys does not fire again and again. Only fixed events are logged.
/// </summary>
public sealed class WindowsGlobalShortcut : IGlobalShortcut
{
    private const int HotKeyId = 0x4C55; // "LU"
    private const uint WmHotKey = 0x0312;
    private const uint WmQuit = 0x0012;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotKeyAlreadyRegistered = 1409;

    private readonly IDiagnosticLog? _log;
    private readonly object _gate = new();
    private Worker? _worker;
    private bool _disposed;

    public WindowsGlobalShortcut(IDiagnosticLog? log = null) => _log = log;

    public event Action? Pressed;

    public Shortcut? Current
    {
        get
        {
            lock (_gate)
                return _worker?.Shortcut;
        }
    }

    /// <summary>The Windows virtual-key code for a key name, or 0.</summary>
    internal static uint VirtualKey(string key) => key switch
    {
        "Space" => 0x20,
        { Length: 1 } when char.IsAsciiLetterUpper(key[0]) || char.IsAsciiDigit(key[0]) => key[0],
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 12 => (uint)(0x70 + f - 1),
        _ => 0
    };

    internal static uint Modifiers(ShortcutModifiers modifiers)
    {
        uint value = ModNoRepeat;
        if (modifiers.HasFlag(ShortcutModifiers.Alt))
            value |= 0x1;
        if (modifiers.HasFlag(ShortcutModifiers.Ctrl))
            value |= 0x2;
        if (modifiers.HasFlag(ShortcutModifiers.Shift))
            value |= 0x4;
        if (modifiers.HasFlag(ShortcutModifiers.Win))
            value |= 0x8;
        return value;
    }

    public async Task<Result> RegisterAsync(Shortcut shortcut, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        if (ct.IsCancellationRequested)
            return Result.Cancelled();
        var vk = VirtualKey(shortcut.Key);
        if (vk == 0)
            return Result.Failure(new OperationError(ErrorCode.InvalidInput, "That key cannot be used for a shortcut."));

        Worker worker;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker?.Shortcut == shortcut)
                return Result.Success();
            _worker?.Stop();
            worker = new Worker(this, shortcut, Modifiers(shortcut.Modifiers), vk);
            _worker = worker;
        }

        var result = await worker.Started.WaitAsync(ct).ConfigureAwait(false);
        if (result.IsFailure)
        {
            lock (_gate)
            {
                if (ReferenceEquals(_worker, worker))
                    _worker = null;
            }

            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "shortcut.register-failed", "Shortcut", "register", result.Error!.Code));
        }

        return result;
    }

    public void Unregister()
    {
        Worker? worker;
        lock (_gate)
        {
            worker = _worker;
            _worker = null;
        }

        worker?.Stop();
    }

    public void Dispose()
    {
        lock (_gate)
            _disposed = true;
        Unregister();
    }

    private void Raise()
    {
        try
        {
            Pressed?.Invoke();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log?.Write(new DiagnosticEvent(DiagnosticLevel.Warning, "shortcut.handler-failed", "Shortcut", "pressed", ErrorCode.Unexpected, ExceptionType: ex.GetType()));
        }
    }

    private sealed class Worker
    {
        private readonly WindowsGlobalShortcut _owner;
        private readonly uint _modifiers;
        private readonly uint _key;
        private readonly TaskCompletionSource<Result> _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Thread _thread;
        private uint _threadId;

        public Worker(WindowsGlobalShortcut owner, Shortcut shortcut, uint modifiers, uint key)
        {
            _owner = owner;
            Shortcut = shortcut;
            _modifiers = modifiers;
            _key = key;
            _thread = new Thread(Run) { IsBackground = true, Name = "Lunar shortcut" };
            _thread.Start();
        }

        public Shortcut Shortcut { get; }

        public Task<Result> Started => _started.Task;

        public void Stop()
        {
            var id = Volatile.Read(ref _threadId);
            if (id != 0)
                PostThreadMessage(id, WmQuit, IntPtr.Zero, IntPtr.Zero);
            if (Thread.CurrentThread != _thread)
                _thread.Join(TimeSpan.FromSeconds(1));
        }

        private void Run()
        {
            // Make sure the queue exists before anyone posts to it.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            Volatile.Write(ref _threadId, GetCurrentThreadId());
            if (!RegisterHotKey(IntPtr.Zero, HotKeyId, _modifiers, _key))
            {
                var error = Marshal.GetLastWin32Error();
                _started.TrySetResult(Result.Failure(error == ErrorHotKeyAlreadyRegistered
                    ? new OperationError(ErrorCode.AccessDenied, "Another app already uses this shortcut. Choose another one.")
                    : new OperationError(ErrorCode.Unexpected, "Windows did not accept the shortcut.")));
                return;
            }

            _started.TrySetResult(Result.Success());
            try
            {
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    if (message.Message == WmHotKey && message.WParam == HotKeyId)
                        _owner.Raise();
                }
            }
            finally
            {
                UnregisterHotKey(IntPtr.Zero, HotKeyId);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg message, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
