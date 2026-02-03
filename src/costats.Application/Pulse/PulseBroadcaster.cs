using costats.Core.Pulse;

namespace costats.Application.Pulse;

public sealed class PulseBroadcaster : IObservable<PulseState>
{
    private readonly object _gate = new();
    private readonly List<IObserver<PulseState>> _observers = new();

    public IDisposable Subscribe(IObserver<PulseState> observer)
    {
        if (observer is null)
        {
            throw new ArgumentNullException(nameof(observer));
        }

        lock (_gate)
        {
            _observers.Add(observer);
        }

        return new Subscription(this, observer);
    }

    public void Publish(PulseState state)
    {
        IObserver<PulseState>[] observers;
        lock (_gate)
        {
            observers = _observers.ToArray();
        }

        foreach (var observer in observers)
        {
            observer.OnNext(state);
        }
    }

    private void Unsubscribe(IObserver<PulseState> observer)
    {
        lock (_gate)
        {
            _observers.Remove(observer);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly PulseBroadcaster _owner;
        private IObserver<PulseState>? _observer;

        public Subscription(PulseBroadcaster owner, IObserver<PulseState> observer)
        {
            _owner = owner;
            _observer = observer;
        }

        public void Dispose()
        {
            var observer = Interlocked.Exchange(ref _observer, null);
            if (observer is not null)
            {
                _owner.Unsubscribe(observer);
            }
        }
    }
}
