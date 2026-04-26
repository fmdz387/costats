using System.Net;
using System.Text;
using costats.Application.Pricing;
using costats.Infrastructure.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace costats.Tests.Pricing;

public sealed class PricingCatalogTests
{
    [Fact]
    public async Task Lookup_uses_fresh_disk_cache()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "litellm.json"), CatalogJson("gpt-5", 0.123m));
        var catalog = CreateCatalog(temp.Path, liteLlm: Response(HttpStatusCode.InternalServerError));

        var pricing = await catalog.LookupAsync("gpt-5", "openai");

        Assert.NotNull(pricing);
        Assert.Equal(0.123m, pricing.InputCostPerToken);
        Assert.Equal(PricingSource.FreshCache, catalog.Status.ActiveSource);
    }

    [Fact]
    public async Task Lookup_refreshes_when_cache_is_stale()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "litellm.json");
        await File.WriteAllTextAsync(path, CatalogJson("gpt-5", 0.123m));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));
        var catalog = CreateCatalog(temp.Path, liteLlm: Response(CatalogJson("gpt-5", 0.456m)));

        var pricing = await catalog.LookupAsync("gpt-5", "openai");

        Assert.NotNull(pricing);
        Assert.Equal(0.456m, pricing.InputCostPerToken);
        Assert.Equal(PricingSource.LiteLlm, catalog.Status.ActiveSource);
        Assert.NotNull(catalog.Status.LastSuccessfulRefreshAt);
    }

    [Fact]
    public async Task Lookup_falls_back_to_openrouter_when_litellm_fails()
    {
        using var temp = new TempDirectory();
        var openRouter = """
        {
          "data": [
            { "id": "openai/gpt-5", "pricing": { "prompt": "0.00000125", "completion": "0.00001", "input_cache_read": "0.000000125" } }
          ]
        }
        """;
        var catalog = CreateCatalog(
            temp.Path,
            liteLlm: Response(HttpStatusCode.InternalServerError),
            openRouter: Response(openRouter));

        var pricing = await catalog.LookupAsync("openai/gpt-5", "openai");

        Assert.NotNull(pricing);
        Assert.Equal(0.00000125m, pricing.InputCostPerToken);
        Assert.Equal(PricingSource.OpenRouter, catalog.Status.ActiveSource);
    }

    [Fact]
    public async Task Lookup_uses_stale_cache_when_network_sources_fail()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "litellm.json");
        await File.WriteAllTextAsync(path, CatalogJson("gpt-5", 0.789m));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddHours(-3));
        var catalog = CreateCatalog(
            temp.Path,
            liteLlm: Response(HttpStatusCode.InternalServerError),
            openRouter: Response(HttpStatusCode.InternalServerError));

        var pricing = await catalog.LookupAsync("gpt-5", "openai");

        Assert.NotNull(pricing);
        Assert.Equal(0.789m, pricing.InputCostPerToken);
        Assert.Equal(PricingSource.StaleCache, catalog.Status.ActiveSource);
    }

    [Fact]
    public async Task Lookup_uses_embedded_snapshot_when_no_cache_or_network_exists()
    {
        using var temp = new TempDirectory();
        var catalog = CreateCatalog(
            temp.Path,
            liteLlm: Response(HttpStatusCode.InternalServerError),
            openRouter: Response(HttpStatusCode.InternalServerError));

        var pricing = await catalog.LookupAsync("claude-opus-4-7", "anthropic");

        Assert.NotNull(pricing);
        Assert.Equal(0.000005m, pricing.InputCostPerToken);
        Assert.Equal(PricingSource.EmbeddedSnapshot, catalog.Status.ActiveSource);
    }

    [Fact]
    public async Task Lookup_tracks_unknown_models_in_status()
    {
        using var temp = new TempDirectory();
        var catalog = CreateCatalog(temp.Path, liteLlm: Response(CatalogJson("gpt-5", 0.123m)));

        var pricing = await catalog.LookupAsync("unknown-model", "openai");

        Assert.Null(pricing);
        Assert.Equal(1, catalog.Status.UnmatchedModelCount);
        Assert.Contains("openai/unknown-model", catalog.Status.UnmatchedModels);
    }

    [Fact]
    public async Task Litellm_parser_filters_github_copilot_entries()
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
        {
          "github_copilot/gpt-5": { "litellm_provider": "github_copilot", "input_cost_per_token": 0 },
          "gpt-5": { "litellm_provider": "openai", "input_cost_per_token": 0.1 }
        }
        """));

        var pricing = await LiteLLMPricingClient.ParseAsync(stream, CancellationToken.None);

        Assert.False(pricing.ContainsKey("github_copilot/gpt-5"));
        Assert.True(pricing.ContainsKey("gpt-5"));
    }

    private static PricingCatalog CreateCatalog(
        string cacheDirectory,
        HttpResponseMessage? liteLlm = null,
        HttpResponseMessage? openRouter = null)
    {
        var options = Options.Create(new PricingOptions
        {
            CacheDirectory = cacheDirectory,
            RefreshHours = 1
        });
        var factory = new StaticHttpClientFactory(new Dictionary<string, HttpResponseMessage?>
        {
            [LiteLLMPricingClient.HttpClientName] = liteLlm,
            [OpenRouterPricingClient.HttpClientName] = openRouter
        });

        return new PricingCatalog(
            new PricingDiskCache(options),
            new LiteLLMPricingClient(factory, options),
            new OpenRouterPricingClient(factory, options),
            new EmbeddedPricingSnapshot(),
            new ModelMatcher(),
            NullLogger<PricingCatalog>.Instance);
    }

    private static string CatalogJson(string model, decimal input) => $$"""
    {
      "{{model}}": {
        "litellm_provider": "{{(model.StartsWith("claude", StringComparison.OrdinalIgnoreCase) ? "anthropic" : "openai")}}",
        "input_cost_per_token": {{input}}
      }
    }
    """;

    private static HttpResponseMessage Response(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Response(HttpStatusCode statusCode) => new(statusCode)
    {
        Content = new StringContent("{}", Encoding.UTF8, "application/json")
    };

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, HttpResponseMessage?> _responses;

        public StaticHttpClientFactory(IReadOnlyDictionary<string, HttpResponseMessage?> responses)
        {
            _responses = responses;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(new StaticHandler(_responses.GetValueOrDefault(name)))
            {
                BaseAddress = new Uri("https://example.invalid")
            };
        }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public StaticHandler(HttpResponseMessage? response)
        {
            _statusCode = response?.StatusCode ?? HttpStatusCode.InternalServerError;
            _content = response?.Content.ReadAsStringAsync().GetAwaiter().GetResult() ?? "{}";
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"costats-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
