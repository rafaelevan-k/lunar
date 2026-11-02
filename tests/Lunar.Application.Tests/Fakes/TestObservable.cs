namespace Lunar.Application.Tests.Fakes;

/// <summary>
/// Reference implementation of the ADR-0006 observable semantics for fakes: no replay, serialized delivery,
/// idempotent unsubscribe, completion on dispose, and never OnError.
/// </summary>
public sealed class TestObservable<T> : IObservable<T>
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
                observer.OnCompleted();
                return NullSubscription.Instance;
            }

            _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    public void Publish(T value)
    {
        lock (_gate)
        {
            foreach (var observer in _observers.ToArray())
                observer.OnNext(value);
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            foreach (var observer in _observers.ToArray())
                observer.OnCompleted();
            _observers.Clear();
        }
    }

    private void Unsubscribe(IObserver<T> observer)
    {
        lock (_gate)
            _observers.Remove(observer);
    }

    private sealed class Subscription(TestObservable<T> owner, IObserver<T> observer) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unsubscribe(observer);
        }
    }

    private sealed class NullSubscription : IDisposable
    {
        public static readonly NullSubscription Instance = new();

        public void Dispose()
        {
        }
    }
}
