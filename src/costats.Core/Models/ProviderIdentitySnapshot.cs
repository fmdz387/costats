namespace costats.Core.Models;

public sealed record ProviderIdentitySnapshot(
    string ProviderId,
    string? DisplayName,
    string? Email,
    string? Organization,
    string? Plan,
    string? LoginMethod);
