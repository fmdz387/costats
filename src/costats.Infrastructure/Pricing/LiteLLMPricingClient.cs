using System.Text.Json;
using costats.Application.Pricing;
using Microsoft.Extensions.Options;

namespace costats.Infrastructure.Pricing;

public sealed class LiteLLMPricingClient
{
    public const string HttpClientName = "LiteLLM";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<PricingOptions> _options;

    public LiteLLMPricingClient(IHttpClientFactory httpClientFactory, IOptions<PricingOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task<IReadOnlyDictionary<string, ModelPricing>> FetchAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await SendWithRetriesAsync(
            () => client.GetAsync(_options.Value.LiteLLMUrl, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await ParseAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyDictionary<string, ModelPricing>> ParseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var raw = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(
            stream,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);

        if (raw is null)
        {
            return new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        }

        var models = new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in raw)
        {
            if (key.StartsWith("github_copilot/", StringComparison.OrdinalIgnoreCase) ||
                value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var pricing = value.Deserialize<ModelPricing>(JsonOptions);
            if (pricing is not null)
            {
                models[key] = pricing;
            }
        }

        return models;
    }

    private static async Task<HttpResponseMessage> SendWithRetriesAsync(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var response = await send().ConfigureAwait(false);
                if ((int)response.StatusCode < 500)
                {
                    return response;
                }

                if (attempt == 2)
                {
                    return response;
                }

                response.Dispose();
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !cancellationToken.IsCancellationRequested)
            {
                lastException = ex;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt)), cancellationToken).ConfigureAwait(false);
        }

        throw lastException ?? new HttpRequestException("LiteLLM pricing request failed.");
    }
}
