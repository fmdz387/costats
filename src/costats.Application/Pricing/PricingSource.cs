namespace costats.Application.Pricing;

public enum PricingSource
{
    Unknown,
    FreshCache,
    LiteLlm,
    OpenRouter,
    StaleCache,
    EmbeddedSnapshot
}

public sealed record PricingStatus(
    PricingSource ActiveSource,
    DateTimeOffset? LastSuccessfulRefreshAt,
    int UnmatchedModelCount,
    IReadOnlyList<string> UnmatchedModels);
