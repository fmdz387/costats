using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public sealed class CodexCliSource : ISignalSource
{
    public ProviderProfile Profile => ProviderCatalog.Codex;

    public Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(new ProviderReading(
            Usage: null,
            Identity: null,
            StatusSummary: "Codex CLI not configured",
            CapturedAt: DateTimeOffset.UtcNow,
            Confidence: ReadingConfidence.Low,
            Source: ReadingSource.Cli));
    }
}
