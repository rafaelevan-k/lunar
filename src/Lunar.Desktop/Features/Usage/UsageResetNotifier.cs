using Lunar.Application.Usage;
using Lunar.Application.Usage.Models;
using Lunar.Desktop.Shell;

namespace Lunar.Desktop.Features.Usage;

/// <summary>What shows the pop-up. The real one is a small window; tests use a stand-in.</summary>
internal interface IResetToastPresenter : IDisposable
{
    /// <summary>The user clicked the pop-up or its close button.</summary>
    event Action? CloseRequested;

    /// <summary>The pointer is over the pop-up, or has left it.</summary>
    event Action? PointerEntered;

    event Action? PointerLeft;

    /// <summary>Shows the content, replacing a pop-up already on screen.</summary>
    void Show(ResetToastContent content);

    void Hide();
}

/// <summary>
/// Decides when the "limit reset" pop-up appears and goes (FE-28, ADR-0014). It listens to <see cref="UsageMonitor.Resets"/> and shows one
/// pop-up per burst of resets: resets that arrive within <see cref="CollectDelay"/> of each other are one message, a newer burst replaces a
/// pop-up still on screen, and a reset noticed more than <see cref="MaxAge"/> after it happened (Lunar was offline or busy) is dropped, because
/// "your limit reset" hours late is noise. The pop-up goes by itself after <see cref="ShowTime"/>; while the pointer is over it, it stays, and
/// leaves <see cref="AfterPointerTime"/> after the pointer does. Turning the page off (the monitor goes Off) clears anything pending and hides
/// it. A pop-up never takes focus, so it cannot interrupt typing; <see cref="Dismiss"/> is how the keyboard closes it. Must be used on the UI
/// thread (monitor callbacks are marshalled through <c>post</c>); dispose to let go.
/// </summary>
internal sealed class UsageResetNotifier : IDisposable
{
    public static readonly TimeSpan CollectDelay = TimeSpan.FromSeconds(1.5);
    public static readonly TimeSpan ShowTime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan AfterPointerTime = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly IResetToastPresenter _presenter;
    private readonly IOneShotTimer _collectTimer;
    private readonly IOneShotTimer _showTimer;
    private readonly Action<Action> _post;
    private readonly List<UsageLimitReset> _pending = [];
    private IDisposable? _resets;
    private IDisposable? _changes;
    private bool _disposed;

    public UsageResetNotifier(UsageMonitor monitor, IResetToastPresenter presenter, IOneShotTimer collectTimer, IOneShotTimer showTimer, Action<Action> post)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(collectTimer);
        ArgumentNullException.ThrowIfNull(showTimer);
        ArgumentNullException.ThrowIfNull(post);
        _presenter = presenter;
        _collectTimer = collectTimer;
        _showTimer = showTimer;
        _post = post;
        presenter.CloseRequested += OnCloseRequested;
        presenter.PointerEntered += OnPointerEntered;
        presenter.PointerLeft += OnPointerLeft;
        _resets = monitor.Resets.Subscribe(new Listener<UsageLimitReset>(reset => _post(() => OnReset(reset))));
        _changes = monitor.Changes.Subscribe(new Listener<UsageState>(state => _post(() => OnState(state))));
    }

    /// <summary>A pop-up is on screen.</summary>
    public bool IsVisible { get; private set; }

    /// <summary>Closes the pop-up and forgets what was waiting to be shown. True when a pop-up was on screen (the key press was used).</summary>
    public bool Dismiss()
    {
        var was = IsVisible;
        Clear();
        return was;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _resets?.Dispose();
        _changes?.Dispose();
        _resets = null;
        _changes = null;
        _presenter.CloseRequested -= OnCloseRequested;
        _presenter.PointerEntered -= OnPointerEntered;
        _presenter.PointerLeft -= OnPointerLeft;
        _collectTimer.Cancel();
        _showTimer.Cancel();
        _presenter.Hide();
        _presenter.Dispose();
        IsVisible = false;
    }

    // Internal so tests can drive it without a monitor thread.
    internal void OnReset(UsageLimitReset reset)
    {
        if (_disposed || reset.DetectedAt - reset.ResetAt > MaxAge)
            return;

        var first = _pending.Count == 0;
        _pending.Add(reset);
        if (first)
            _collectTimer.Schedule(CollectDelay, Flush);
    }

    internal void OnState(UsageState state)
    {
        if (!_disposed && state.Status == UsageStatus.Off)
            Clear();
    }

    private void Flush()
    {
        if (_disposed || _pending.Count == 0)
            return;
        var content = ResetToastContent.For(_pending);
        _pending.Clear();
        _presenter.Show(content);
        IsVisible = true;
        _showTimer.Schedule(ShowTime, Clear);
    }

    private void Clear()
    {
        _pending.Clear();
        _collectTimer.Cancel();
        _showTimer.Cancel();
        if (IsVisible)
        {
            IsVisible = false;
            _presenter.Hide();
        }
    }

    private void OnCloseRequested()
    {
        if (!_disposed)
            Clear();
    }

    private void OnPointerEntered()
    {
        if (!_disposed && IsVisible)
            _showTimer.Cancel();
    }

    private void OnPointerLeft()
    {
        if (!_disposed && IsVisible)
            _showTimer.Schedule(AfterPointerTime, Clear);
    }

    private sealed class Listener<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);

        public void OnError(Exception error)
        {
        }

        public void OnCompleted()
        {
        }
    }
}
