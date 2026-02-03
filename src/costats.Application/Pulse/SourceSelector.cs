using costats.Core.Pulse;
using Microsoft.Extensions.Logging;

namespace costats.Application.Pulse;

public sealed class SourceSelector : ISourceSelector
{
    private readonly ILogger<SourceSelector> _logger;

    public SourceSelector(ILogger<SourceSelector> logger)
    {
        _logger = logger;
    }

    public async Task<ProviderReading> SelectAsync(
        string providerId,
        IReadOnlyList<ISignalSource> sources,
        CancellationToken cancellationToken)
    {
        ProviderReading? best = null;

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var reading = await source.ReadAsync(cancellationToken);
                if (best is null || IsBetter(reading, best))
                {
                    best = reading;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Source {Source} failed for {ProviderId}", source.GetType().Name, providerId);
            }
        }

        return best ?? new ProviderReading(
            Usage: null,
            Identity: null,
            StatusSummary: "No data",
            CapturedAt: DateTimeOffset.UtcNow,
            Confidence: ReadingConfidence.Unknown,
            Source: ReadingSource.Unknown);
    }

    private static bool IsBetter(ProviderReading candidate, ProviderReading current)
    {
        if (candidate.Confidence != current.Confidence)
        {
            return candidate.Confidence > current.Confidence;
        }

        return candidate.CapturedAt > current.CapturedAt;
    }
}
