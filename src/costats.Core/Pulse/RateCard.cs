namespace costats.Core.Pulse;

/// <summary>
/// Defines per-token pricing for a specific model.
/// All rates are in USD per single token.
/// </summary>
public sealed record ModelRateCard
{
    public required decimal InputRate { get; init; }
    public required decimal OutputRate { get; init; }
    public required decimal CacheReadRate { get; init; }
    public decimal CacheWriteRate { get; init; } // For Claude cache creation

    // Tiered pricing (optional) - rates above threshold
    public int? TierThreshold { get; init; }
    public decimal? InputRateAboveTier { get; init; }
    public decimal? OutputRateAboveTier { get; init; }
    public decimal? CacheReadRateAboveTier { get; init; }
    public decimal? CacheWriteRateAboveTier { get; init; }

    /// <summary>
    /// Computes total cost for a token ledger using this rate card.
    /// </summary>
    public decimal ComputeCost(TokenLedger ledger)
    {
        var inputCost = ComputeTieredCost(ledger.StandardInput, InputRate, InputRateAboveTier);
        var outputCost = ComputeTieredCost(ledger.GeneratedOutput, OutputRate, OutputRateAboveTier);
        var cacheReadCost = ComputeTieredCost(ledger.CachedInput, CacheReadRate, CacheReadRateAboveTier);
        var cacheWriteCost = ComputeTieredCost(ledger.CacheWriteInput, CacheWriteRate, CacheWriteRateAboveTier);

        return inputCost + outputCost + cacheReadCost + cacheWriteCost;
    }

    private decimal ComputeTieredCost(int tokens, decimal baseRate, decimal? aboveRate)
    {
        if (tokens <= 0) return 0;

        if (TierThreshold is not { } threshold || aboveRate is not { } above)
        {
            return tokens * baseRate;
        }

        var belowCount = Math.Min(tokens, threshold);
        var aboveCount = Math.Max(0, tokens - threshold);

        return (belowCount * baseRate) + (aboveCount * above);
    }
}

/// <summary>
/// Central registry of model pricing information.
/// </summary>
public static class TariffRegistry
{
    private static readonly Dictionary<string, ModelRateCard> ClaudeRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-haiku-4-5"] = new ModelRateCard
        {
            InputRate = 0.000001m,
            OutputRate = 0.000005m,
            CacheReadRate = 0.0000001m,
            CacheWriteRate = 0.00000125m
        },
        ["claude-sonnet-4-5"] = new ModelRateCard
        {
            InputRate = 0.000003m,
            OutputRate = 0.000015m,
            CacheReadRate = 0.0000003m,
            CacheWriteRate = 0.00000375m,
            TierThreshold = 200_000,
            InputRateAboveTier = 0.000006m,
            OutputRateAboveTier = 0.0000225m,
            CacheReadRateAboveTier = 0.0000006m,
            CacheWriteRateAboveTier = 0.0000075m
        },
        ["claude-opus-4-5"] = new ModelRateCard
        {
            InputRate = 0.000005m,
            OutputRate = 0.000025m,
            CacheReadRate = 0.0000005m,
            CacheWriteRate = 0.00000625m
        },
        ["claude-opus-4"] = new ModelRateCard
        {
            InputRate = 0.000015m,
            OutputRate = 0.000075m,
            CacheReadRate = 0.0000015m,
            CacheWriteRate = 0.00001875m
        },
        ["claude-sonnet-4"] = new ModelRateCard
        {
            InputRate = 0.000003m,
            OutputRate = 0.000015m,
            CacheReadRate = 0.0000003m,
            CacheWriteRate = 0.00000375m
        }
    };

    private static readonly Dictionary<string, ModelRateCard> CodexRates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gpt-5"] = new ModelRateCard
        {
            InputRate = 0.00000125m,
            OutputRate = 0.00001m,
            CacheReadRate = 0.000000125m
        },
        ["gpt-5.2"] = new ModelRateCard
        {
            InputRate = 0.00000175m,
            OutputRate = 0.000014m,
            CacheReadRate = 0.000000175m
        },
        ["o3"] = new ModelRateCard
        {
            InputRate = 0.00001m,
            OutputRate = 0.00004m,
            CacheReadRate = 0.0000025m
        },
        ["o4-mini"] = new ModelRateCard
        {
            InputRate = 0.0000011m,
            OutputRate = 0.0000044m,
            CacheReadRate = 0.000000275m
        }
    };

    // Fallback rates for unknown models (conservative estimate)
    private static readonly ModelRateCard ClaudeFallbackRate = new()
    {
        InputRate = 0.000003m,
        OutputRate = 0.000015m,
        CacheReadRate = 0.0000003m,
        CacheWriteRate = 0.00000375m
    };

    private static readonly ModelRateCard CodexFallbackRate = new()
    {
        InputRate = 0.0000015m,
        OutputRate = 0.000012m,
        CacheReadRate = 0.00000015m
    };

    /// <summary>
    /// Finds the rate card for a Claude model. Returns fallback if not found.
    /// </summary>
    public static ModelRateCard FindClaudeRate(string rawModelName)
    {
        var normalized = NormalizeClaudeModel(rawModelName);
        return ClaudeRates.GetValueOrDefault(normalized) ?? ClaudeFallbackRate;
    }

    /// <summary>
    /// Finds the rate card for a Codex/OpenAI model. Returns fallback if not found.
    /// </summary>
    public static ModelRateCard FindCodexRate(string rawModelName)
    {
        var normalized = NormalizeCodexModel(rawModelName);
        return CodexRates.GetValueOrDefault(normalized) ?? CodexFallbackRate;
    }

    /// <summary>
    /// Strips vendor prefixes and date suffixes from Claude model names.
    /// </summary>
    private static string NormalizeClaudeModel(string raw)
    {
        var trimmed = raw.Trim();

        // Remove "anthropic." prefix
        if (trimmed.StartsWith("anthropic.", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[10..];

        // Remove date suffix like "-20251101"
        var datePattern = System.Text.RegularExpressions.Regex.Match(trimmed, @"-\d{8}$");
        if (datePattern.Success)
        {
            var candidate = trimmed[..datePattern.Index];
            if (ClaudeRates.ContainsKey(candidate))
                return candidate;
        }

        return trimmed;
    }

    /// <summary>
    /// Strips vendor prefixes and "-codex" suffix from Codex model names.
    /// </summary>
    private static string NormalizeCodexModel(string raw)
    {
        var trimmed = raw.Trim();

        // Remove "openai/" prefix
        if (trimmed.StartsWith("openai/", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..];

        // Remove "-codex" suffix
        var codexIdx = trimmed.IndexOf("-codex", StringComparison.OrdinalIgnoreCase);
        if (codexIdx > 0)
        {
            var candidate = trimmed[..codexIdx];
            if (CodexRates.ContainsKey(candidate))
                return candidate;
        }

        return trimmed;
    }
}
