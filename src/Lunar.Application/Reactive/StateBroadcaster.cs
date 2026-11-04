namespace Lunar.Application.Reactive;

/// <summary>
/// The observable half of every state port and state-owning use case (ADR-0006 semantics): no replay, delivery serialized under
/// one lock so observers never see two calls at once or out of order, idempotent and thread-safe unsubscribe, and
/// <c>OnCompleted</c> when the owner closes it. <c>OnError</c> is never called. A throwing observer is dropped from that
/// notification only: it neither stops the others nor reaches the caller. Safe to publish from any thread.
/// </summary>
public sealed class StateBroadcaster<T> : IObservable<T>
{
    private readonly object _gate = new();
    private readonly List<IObserver<T>> _observers = [];
    private bool _completed;

    public int ObserverCount
    {
        get
        {
            lock (_gate)
                return _observers.Count;
        }
    }

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate)
        {
            if (_completed)
            {
                Safely(observer.OnCompleted);
                return EmptySubscription.Instance;
            }

            _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    /// <summary>Delivers a value to the current observers; ignored once completed.</summary>
    public void Publish(T value)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            foreach (var observer in _observers.ToArray())
                Safely(() => observer.OnNext(value));
        }
    }

    /// <summary>Completes every observer once and refuses later values and subscriptions' values.</summary>
    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            foreach (var observer in _observers.ToArray())
                Safely(observer.OnCompleted);
            _observers.Clear();
        }
    }

    private static void Safely(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
            // An observer's own failure must not break delivery to the others or the producer's thread.
        }
    }

    private void Remove(IObserver<T> observer)
    {
        lock (_gate)
            _observers.Remove(observer);
    }

    private sealed class Subscription(StateBroadcaster<T> owner, IObserver<T> observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Remove(observer);
        }
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static EmptySubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
