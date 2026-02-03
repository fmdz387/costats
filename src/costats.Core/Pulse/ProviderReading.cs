namespace costats.Core.Pulse;

public sealed record ProviderReading(
    UsagePulse? Usage,
    IdentityCard? Identity,
    string? StatusSummary,
    DateTimeOffset CapturedAt,
    ReadingConfidence Confidence,
    ReadingSource Source);
