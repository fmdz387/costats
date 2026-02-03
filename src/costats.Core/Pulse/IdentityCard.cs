namespace costats.Core.Pulse;

public sealed record IdentityCard(
    string ProviderId,
    string? DisplayName,
    string? Email,
    string? Org,
    string? Plan,
    string? LoginMethod);
