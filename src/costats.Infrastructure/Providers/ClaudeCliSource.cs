using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public sealed class ClaudeCliSource : ISignalSource
{
    public ProviderProfile Profile => ProviderCatalog.Claude;

    public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ProviderReading(
            Usage: null,
            Identity: null,
            StatusSummary: "Claude CLI not configured",
            CapturedAt: DateTimeOffset.UtcNow,
            Confidence: ReadingConfidence.Low,
            Source: ReadingSource.Cli));
    }
}
