namespace costats.Core.Models;

public sealed record ProviderSnapshot(
    UsageSnapshot? Usage,
    ProviderIdentitySnapshot? Identity,
    string? StatusSummary,
    DateTimeOffset CapturedAt);
