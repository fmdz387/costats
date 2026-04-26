using costats.Application.Pricing;
using costats.Infrastructure.Expense;
using Xunit;

namespace costats.Tests.Pricing;

public sealed class LogDigestorTests
{
    [Fact]
    public async Task DigestCodexLogs_extracts_reasoning_output_tokens()
    {
        using var temp = new TempDirectory();
        var day = new DateOnly(2026, 4, 25);
        var dayDir = Path.Combine(temp.Path, "2026", "04", "25");
        Directory.CreateDirectory(dayDir);
        await File.WriteAllTextAsync(Path.Combine(dayDir, "session.jsonl"), """
        {"type":"turn_context","payload":{"model":"gpt-5"}}
        {"type":"event_msg","timestamp":"2026-04-25T12:00:00Z","payload":{"type":"token_count","info":{"last_token_usage":{"input_tokens":100,"cached_input_tokens":20,"output_tokens":30,"reasoning_output_tokens":12}}}}
        """);

        var slices = await LogDigestor.DigestCodexLogsAsync(new StaticPricingCatalog(), temp.Path, day, day);

        var slice = Assert.Single(slices);
        Assert.Equal(80, slice.Tokens.StandardInput);
        Assert.Equal(20, slice.Tokens.CachedInput);
        Assert.Equal(18, slice.Tokens.GeneratedOutput);
        Assert.Equal(12, slice.Tokens.ReasoningOutput);
        Assert.Equal(390m, slice.ComputedCostUsd);
    }

    [Fact]
    public async Task DigestClaudeLogs_extracts_nested_reasoning_tokens()
    {
        using var temp = new TempDirectory();
        var projectDir = Path.Combine(temp.Path, "project");
        Directory.CreateDirectory(projectDir);
        await File.WriteAllTextAsync(Path.Combine(projectDir, "chat.jsonl"), """
        {"type":"assistant","timestamp":"2026-04-25T12:00:00Z","requestId":"r1","message":{"id":"m1","model":"claude-3.5-sonnet","usage":{"input_tokens":50,"cache_read_input_tokens":10,"cache_creation_input_tokens":5,"output_tokens":20,"output_tokens_details":{"reasoning_tokens":7}}}}
        """);

        var day = new DateOnly(2026, 4, 25);
        var slices = await LogDigestor.DigestClaudeLogsAsync(new StaticPricingCatalog(), temp.Path, day, day);

        var slice = Assert.Single(slices);
        Assert.Equal(50, slice.Tokens.StandardInput);
        Assert.Equal(10, slice.Tokens.CachedInput);
        Assert.Equal(5, slice.Tokens.CacheWriteInput);
        Assert.Equal(13, slice.Tokens.GeneratedOutput);
        Assert.Equal(7, slice.Tokens.ReasoningOutput);
    }

    [Fact]
    public async Task CopilotOtelDigestor_reads_attribute_array_shape()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "otel.jsonl"), """
        {"resourceSpans":[{"scopeSpans":[{"spans":[{"timeUnixNano":"1777118400000000000","attributes":[{"key":"gen_ai.request.model","value":{"stringValue":"gpt-5"}},{"key":"gen_ai.usage.input_tokens","value":{"intValue":"40"}},{"key":"gen_ai.usage.cached_input_tokens","value":{"intValue":"15"}},{"key":"gen_ai.usage.output_tokens","value":{"intValue":"10"}},{"key":"gen_ai.usage.reasoning_output_tokens","value":{"intValue":"4"}}]}]}]}]}
        """);

        var day = new DateOnly(2026, 4, 25);
        var slices = await CopilotOtelDigestor.DigestAsync(new StaticPricingCatalog(), [temp.Path], day, day);

        var slice = Assert.Single(slices);
        Assert.Equal(25, slice.Tokens.StandardInput);
        Assert.Equal(15, slice.Tokens.CachedInput);
        Assert.Equal(6, slice.Tokens.GeneratedOutput);
        Assert.Equal(4, slice.Tokens.ReasoningOutput);
    }

    private sealed class StaticPricingCatalog : IPricingCatalog
    {
        private static readonly ModelPricing Pricing = new()
        {
            InputCostPerToken = 1m,
            OutputCostPerToken = 10m,
            CacheReadInputTokenCost = 0.5m,
            CacheCreationInputTokenCost = 2m
        };

        public ValueTask<ModelPricing?> LookupAsync(string modelId, string? providerHint = null, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<ModelPricing?>(Pricing);
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"costats-log-tests-{Guid.NewGuid():N}");
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
