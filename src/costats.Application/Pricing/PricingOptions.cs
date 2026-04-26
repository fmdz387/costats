namespace costats.Application.Pricing;

public sealed class PricingOptions
{
    public const string SectionName = "Costats:Pricing";

    public double RefreshHours { get; set; } = 24;

    public string LiteLLMUrl { get; set; } =
        "https://raw.githubusercontent.com/BerriAI/litellm/main/model_prices_and_context_window.json";

    public string OpenRouterUrl { get; set; } = "https://openrouter.ai/api/v1/models";

    public string? CacheDirectory { get; set; }

    public TimeSpan RefreshInterval => TimeSpan.FromHours(Math.Max(0.05, RefreshHours));

    public string GetCacheDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CacheDirectory))
        {
            return Environment.ExpandEnvironmentVariables(CacheDirectory);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "costats",
            "pricing");
    }
}
