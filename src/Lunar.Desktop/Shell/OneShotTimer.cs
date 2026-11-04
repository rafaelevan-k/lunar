using System.Windows.Threading;

namespace Lunar.Desktop.Shell;

/// <summary>A one-shot timer, so animation and clock code can be driven by hand in tests.</summary>
internal interface IOneShotTimer : IDisposable
{
    /// <summary>Calls <paramref name="callback"/> once after <paramref name="delay"/>, replacing any pending call.</summary>
    void Schedule(TimeSpan delay, Action callback);

    /// <summary>Drops the pending call, if any.</summary>
    void Cancel();
}

/// <summary>
/// One-shot <see cref="DispatcherTimer"/>. It is stopped after every tick and when cancelled or disposed, so an idle
/// or hidden island holds no running timer.
/// </summary>
internal sealed class DispatcherOneShotTimer : IOneShotTimer
{
    private readonly DispatcherTimer _timer;
    private Action? _callback;

    public DispatcherOneShotTimer(Dispatcher? dispatcher = null)
    {
        _timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher ?? Dispatcher.CurrentDispatcher);
        _timer.Tick += OnTick;
    }

    public void Schedule(TimeSpan delay, Action callback)
    {
        _timer.Stop();
        _callback = callback;
        _timer.Interval = delay > TimeSpan.Zero ? delay : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }

    public void Cancel()
    {
        _timer.Stop();
        _callback = null;
    }

    public void Dispose()
    {
        Cancel();
        _timer.Tick -= OnTick;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        var callback = _callback;
        _callback = null;
        callback?.Invoke();
    }
}
