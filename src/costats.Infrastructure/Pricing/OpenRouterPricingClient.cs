using System.Globalization;
using System.Text.Json;
using costats.Application.Pricing;
using Microsoft.Extensions.Options;

namespace costats.Infrastructure.Pricing;

public sealed class OpenRouterPricingClient
{
    public const string HttpClientName = "OpenRouter";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<PricingOptions> _options;

    public OpenRouterPricingClient(IHttpClientFactory httpClientFactory, IOptions<PricingOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<IReadOnlyDictionary<string, ModelPricing>> FetchAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client.GetAsync(_options.Value.OpenRouterUrl, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyDictionary<string, ModelPricing>> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        }

        var models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            if (!TryGetString(item, "id", out var id) ||
                !item.TryGetProperty("pricing", out var pricing) ||
                pricing.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            models[id] = new ModelPricing
            {
                InputCostPerToken = GetDecimalOrNull(pricing, "prompt"),
                OutputCostPerToken = GetDecimalOrNull(pricing, "completion"),
                ReasoningOutputCostPerToken = GetDecimalOrNull(pricing, "internal_reasoning"),
                CacheReadInputTokenCost = GetDecimalOrNull(pricing, "input_cache_read"),
                CacheCreationInputTokenCost = GetDecimalOrNull(pricing, "input_cache_write"),
                LiteLlmProvider = ProviderFromModelId(id)
            };
        }

        return models;
    }

    private static string? ProviderFromModelId(string id)
    {
        var separator = id.IndexOf('/');
        return separator > 0 ? id[..separator] : null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return value.Length > 0;
        }

        value = string.Empty;
        return false;
    }

    private static decimal? GetDecimalOrNull(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var prop))
        {
            return null;
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number when prop.TryGetDecimal(out var value) => value,
            JsonValueKind.String when decimal.TryParse(prop.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) => value,
            _ => null
        };
    }
}
