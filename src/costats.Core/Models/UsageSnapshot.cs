namespace costats.Core.Models;

public sealed record UsageSnapshot(
    string ProviderId,
    DateTimeOffset CapturedAt,
    long? SessionUsed,
    long? SessionLimit,
    long? WeeklyUsed,
    long? WeeklyLimit,
    decimal? CreditsRemaining,
    RateWindow? SessionWindow,
    RateWindow? WeeklyWindow);
