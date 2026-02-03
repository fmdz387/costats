using costats.Application.Abstractions;
using costats.Core.Pulse;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace costats.Application.Pulse;

public sealed class PulseOrchestrator : BackgroundService, IPulseOrchestrator
{
    private readonly IEnumerable<ISignalSource> _sources;
    private readonly ISourceSelector _selector;
    private readonly IClock _clock;
    private readonly PulseBroadcaster _broadcaster;
    private readonly IPulseSnapshotWriter _snapshotWriter;
    private readonly ILogger<PulseOrchestrator> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _intervalLock = new();

    private TimeSpan _refreshInterval;
    private CancellationTokenSource? _timerCts;

    public PulseOrchestrator(
        IEnumerable<ISignalSource> sources,
        ISourceSelector selector,
        IClock clock,
        PulseBroadcaster broadcaster,
        IPulseSnapshotWriter snapshotWriter,
        IOptions<PulseOptions> options,
        ILogger<PulseOrchestrator> logger)
    {
        _sources = sources;
        _selector = selector;
        _clock = clock;
        _broadcaster = broadcaster;
        _snapshotWriter = snapshotWriter;
        _refreshInterval = options.Value.RefreshInterval;
        _logger = logger;
    }

    public IObservable<PulseState> PulseStream => _broadcaster;

    public void UpdateRefreshInterval(TimeSpan interval)
    {
        lock (_intervalLock)
        {
            _refreshInterval = interval;
            // Cancel current timer to restart with new interval
            _timerCts?.Cancel();
        }
        _logger.LogInformation("Refresh interval updated to {Interval}", interval);
    }

    public async Task RefreshOnceAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var byProvider = _sources
                .GroupBy(source => source.Profile.ProviderId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ISignalSource>)group.ToList());

            var providerReads = new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();

            foreach (var (providerId, providerSources) in byProvider)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var reading = await _selector.SelectAsync(providerId, providerSources, cancellationToken);
                providerReads[providerId] = reading;
            }

            var state = new PulseState(providerReads, _clock.UtcNow, errors);
            _broadcaster.Publish(state);
            await _snapshotWriter.WriteAsync(state, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pulse refresh failed");
            var state = new PulseState(
                new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase),
                _clock.UtcNow,
                new List<string> { ex.Message });
            _broadcaster.Publish(state);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshOnceAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan currentInterval;
            lock (_intervalLock)
            {
                currentInterval = _refreshInterval;
                _timerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            }

            try
            {
                using var timer = new PeriodicTimer(currentInterval);
                while (await timer.WaitForNextTickAsync(_timerCts.Token))
                {
                    await RefreshOnceAsync(_timerCts.Token);
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // Timer was cancelled due to interval change, restart with new interval
                _logger.LogDebug("Restarting timer with new interval");
            }
        }
    }
}
