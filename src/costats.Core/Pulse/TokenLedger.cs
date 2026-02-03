namespace costats.Core.Pulse;

/// <summary>
/// Tracks token consumption across different categories.
/// </summary>
public sealed record TokenLedger
{
    public required int StandardInput { get; init; }
    public required int CachedInput { get; init; }
    public required int GeneratedOutput { get; init; }
    public int CacheWriteInput { get; init; } // Claude-specific

    public int TotalConsumed => StandardInput + CachedInput + GeneratedOutput + CacheWriteInput;
    public int NetInput => StandardInput + CacheWriteInput; // Excludes cache reads

    public static TokenLedger Empty => new()
    {
        StandardInput = 0,
        CachedInput = 0,
        GeneratedOutput = 0,
        CacheWriteInput = 0
    };

    public TokenLedger Combine(TokenLedger other) => new()
    {
        StandardInput = StandardInput + other.StandardInput,
        CachedInput = CachedInput + other.CachedInput,
        GeneratedOutput = GeneratedOutput + other.GeneratedOutput,
        CacheWriteInput = CacheWriteInput + other.CacheWriteInput
    };
}

/// <summary>
/// Consumption record for a specific time period with cost attached.
/// </summary>
public sealed record ConsumptionSlice
{
    public required DateOnly Period { get; init; }
    public required string ModelIdentifier { get; init; }
    public required TokenLedger Tokens { get; init; }
    public required decimal ComputedCostUsd { get; init; }
}

/// <summary>
/// Aggregated consumption summary over a time window.
/// </summary>
public sealed record ConsumptionDigest
{
    public required TokenLedger TodayTokens { get; init; }
    public required decimal TodayCostUsd { get; init; }
    public required TokenLedger RollingWindowTokens { get; init; }
    public required decimal RollingWindowCostUsd { get; init; }
    public required int RollingWindowDays { get; init; }
    public required IReadOnlyList<ConsumptionSlice> DailyBreakdown { get; init; }
    public required DateTimeOffset ComputedAt { get; init; }

    public static ConsumptionDigest None => new()
    {
        TodayTokens = TokenLedger.Empty,
        TodayCostUsd = 0,
        RollingWindowTokens = TokenLedger.Empty,
        RollingWindowCostUsd = 0,
        RollingWindowDays = 30,
        DailyBreakdown = [],
        ComputedAt = DateTimeOffset.UtcNow
    };
}
