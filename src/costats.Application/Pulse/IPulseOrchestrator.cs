using costats.Core.Pulse;

namespace costats.Application.Pulse;

public interface IPulseOrchestrator
{
    IObservable<PulseState> PulseStream { get; }

    Task RefreshOnceAsync(CancellationToken cancellationToken);

    void UpdateRefreshInterval(TimeSpan interval);
}
