using System.Windows.Threading;

namespace Lunar.Desktop.Features.Eyes;

/// <summary>A one-shot timer the animation controller uses, so tests can drive time by hand.</summary>
internal interface IEyeTimer : IDisposable
{
    /// <summary>Calls <paramref name="callback"/> once after <paramref name="delay"/>, replacing any pending call.</summary>
    void Schedule(TimeSpan delay, Action callback);

    /// <summary>Drops the pending call, if any.</summary>
    void Cancel();
}

/// <summary>Random numbers in [0, 1) so tests can script every draw.</summary>
internal interface IRandomSource
{
    double NextDouble();
}

internal sealed class SystemRandomSource : IRandomSource
{
    public static SystemRandomSource Instance { get; } = new();

    public double NextDouble() => Random.Shared.NextDouble();
}

/// <summary>
/// One-shot <see cref="DispatcherTimer"/>. It is stopped after every tick and when cancelled or disposed, so an idle
/// or hidden island holds no running timer.
/// </summary>
internal sealed class DispatcherEyeTimer : IEyeTimer
{
    private readonly DispatcherTimer _timer;
    private Action? _callback;

    public DispatcherEyeTimer(Dispatcher? dispatcher = null)
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
