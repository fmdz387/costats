using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using costats.Application.Pricing;
using costats.Core.Pulse;
using costats.Infrastructure.Pricing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace costats.Tests.Pricing;

public sealed class TokscaleParityTests
{
    [Fact]
    public async Task Live_catalog_matches_tokscale_pricing_and_costs_for_curated_records()
    {
        if (!await IsTokscaleAvailableAsync())
        {
            return;
        }

        using var temp = new TempDirectory();
        var catalog = CreateLiveCatalog(temp.Path);
        var records = await LoadRecordsAsync();

        foreach (var record in records)
        {
            var tokscalePricing = await GetTokscalePricingAsync(record.Model);
            if (tokscalePricing is null)
            {
                continue;
            }

            var actualPricing = await catalog.LookupAsync(record.Model, record.Provider);
            Assert.NotNull(actualPricing);

            Assert.Equal(tokscalePricing.InputCostPerToken, actualPricing.InputCostPerToken);
            Assert.Equal(tokscalePricing.OutputCostPerToken, actualPricing.OutputCostPerToken);
            Assert.Equal(tokscalePricing.ReasoningOutputCostPerToken, actualPricing.ReasoningOutputCostPerToken);
            Assert.Equal(tokscalePricing.CacheReadInputTokenCost, actualPricing.CacheReadInputTokenCost);
            Assert.Equal(tokscalePricing.CacheCreationInputTokenCost, actualPricing.CacheCreationInputTokenCost);

            var ledger = record.ToLedger();
            var expected = ComputeCost(tokscalePricing, ledger);
            var actual = PricingCostCalculator.ComputeCost(actualPricing, ledger);
            Assert.Equal(decimal.Round(expected, 6), decimal.Round(actual, 6));
        }
    }

    private static PricingCatalog CreateLiveCatalog(string cacheDirectory)
    {
        var options = Options.Create(new PricingOptions
        {
            CacheDirectory = cacheDirectory,
            RefreshHours = 1
        });
        var factory = new DefaultHttpClientFactory();

        return new PricingCatalog(
            new PricingDiskCache(options),
            new LiteLLMPricingClient(factory, options),
            new OpenRouterPricingClient(factory, options),
            new EmbeddedPricingSnapshot(),
            new ModelMatcher(),
            NullLogger<PricingCatalog>.Instance);
    }

    private static decimal ComputeCost(ModelPricing pricing, TokenLedger ledger)
    {
        return Tiered(ledger.StandardInput, pricing.InputCostPerToken, pricing.InputCostPerTokenAbove200k)
            + Tiered(ledger.GeneratedOutput, pricing.OutputCostPerToken, pricing.OutputCostPerTokenAbove200k)
            + Tiered(ledger.ReasoningOutput, pricing.ReasoningOutputCostPerToken ?? pricing.OutputCostPerToken, pricing.ReasoningOutputCostPerTokenAbove200k ?? pricing.OutputCostPerTokenAbove200k)
            + Tiered(ledger.CachedInput, pricing.CacheReadInputTokenCost, pricing.CacheReadInputTokenCostAbove200k)
            + Tiered(ledger.CacheWriteInput, pricing.CacheCreationInputTokenCost, pricing.CacheCreationInputTokenCostAbove200k);

        static decimal Tiered(int tokens, decimal? baseRate, decimal? aboveRate)
        {
            const int tier = 200_000;
            if (tokens <= 0)
            {
                return 0m;
            }

            var b = baseRate ?? 0m;
            return tokens <= tier || aboveRate is null
                ? tokens * b
                : (tier * b) + ((tokens - tier) * aboveRate.Value);
        }
    }

    private static async Task<IReadOnlyList<UsageRecord>> LoadRecordsAsync()
    {
        var fixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "usage-records.json");
        await using var stream = File.OpenRead(fixturePath);
        return await JsonSerializer.DeserializeAsync<List<UsageRecord>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? [];
    }

    private static async Task<ModelPricing?> GetTokscalePricingAsync(string model)
    {
        var result = await RunProcessAsync("npx", ["tokscale", "pricing", "--json", "--no-spinner", model]);
        if (result.ExitCode != 0)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(result.Stdout);
        if (!doc.RootElement.TryGetProperty("pricing", out var pricing))
        {
            return null;
        }

        return JsonSerializer.Deserialize<TokscalePricing>(pricing.GetRawText(), new JsonSerializerOptions(JsonSerializerDefaults.Web))?.ToModelPricing();
    }

    private static async Task<bool> IsTokscaleAvailableAsync()
    {
        try
        {
            var result = await RunProcessAsync("npx", ["tokscale", "--version"]);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments)
    {
        using var actualProcess = Process.Start(CreateStartInfo(fileName, arguments));
        Assert.NotNull(actualProcess);
        var stdoutTask = actualProcess.StandardOutput.ReadToEndAsync();
        var stderrTask = actualProcess.StandardError.ReadToEndAsync();
        await actualProcess.WaitForExitAsync();
        return new ProcessResult(actualProcess.ExitCode, await stdoutTask, await stderrTask);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);

    private sealed class DefaultHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new()
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private sealed record UsageRecord(
        string Provider,
        string Model,
        int StandardInput,
        int CachedInput,
        int CacheWriteInput,
        int GeneratedOutput,
        int ReasoningOutput = 0)
    {
        public TokenLedger ToLedger() => new()
        {
            StandardInput = StandardInput,
            CachedInput = CachedInput,
            CacheWriteInput = CacheWriteInput,
            GeneratedOutput = GeneratedOutput,
            ReasoningOutput = ReasoningOutput
        };
    }

    private sealed record TokscalePricing
    {
        [JsonPropertyName("inputCostPerToken")]
        public decimal? InputCostPerToken { get; init; }

        [JsonPropertyName("outputCostPerToken")]
        public decimal? OutputCostPerToken { get; init; }

        [JsonPropertyName("reasoningOutputCostPerToken")]
        public decimal? ReasoningOutputCostPerToken { get; init; }

        [JsonPropertyName("reasoningOutputCostPerTokenAbove200k")]
        public decimal? ReasoningOutputCostPerTokenAbove200k { get; init; }

        [JsonPropertyName("cacheReadInputTokenCost")]
        public decimal? CacheReadInputTokenCost { get; init; }

        [JsonPropertyName("cacheCreationInputTokenCost")]
        public decimal? CacheCreationInputTokenCost { get; init; }

        public ModelPricing ToModelPricing() => new()
        {
            InputCostPerToken = InputCostPerToken,
            OutputCostPerToken = OutputCostPerToken,
            ReasoningOutputCostPerToken = ReasoningOutputCostPerToken,
            ReasoningOutputCostPerTokenAbove200k = ReasoningOutputCostPerTokenAbove200k,
            CacheReadInputTokenCost = CacheReadInputTokenCost,
            CacheCreationInputTokenCost = CacheCreationInputTokenCost
        };
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"costats-tokscale-{Guid.NewGuid():N}");
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
