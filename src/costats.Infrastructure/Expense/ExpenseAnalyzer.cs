using costats.Core.Pulse;

namespace costats.Infrastructure.Expense;

/// <summary>
/// Analyzes token consumption and produces digest summaries.
/// </summary>
public sealed class ExpenseAnalyzer
{
    private const int DefaultWindowDays = 30;

    /// <summary>
    /// Produces a consumption digest for Claude Code.
    /// </summary>
    public async Task<ConsumptionDigest> AnalyzeClaudeAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var windowStart = today.AddDays(-(DefaultWindowDays - 1));

        var slices = await LogDigestor.DigestClaudeLogsAsync(windowStart, today, cancellationToken).ConfigureAwait(false);
        return BuildDigest(slices, today, DefaultWindowDays);
    }

    /// <summary>
    /// Produces a consumption digest for Codex.
    /// </summary>
    public async Task<ConsumptionDigest> AnalyzeCodexAsync(CancellationToken cancellationToken = default)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var windowStart = today.AddDays(-(DefaultWindowDays - 1));

        var slices = await LogDigestor.DigestCodexLogsAsync(windowStart, today, cancellationToken).ConfigureAwait(false);
        return BuildDigest(slices, today, DefaultWindowDays);
    }

    private static ConsumptionDigest BuildDigest(
        IReadOnlyList<ConsumptionSlice> slices,
        DateOnly today,
        int windowDays)
    {
        if (slices.Count == 0)
            return ConsumptionDigest.None;

        // Today's consumption
        var todaySlices = slices.Where(s => s.Period == today).ToList();
        var todayTokens = todaySlices.Aggregate(TokenLedger.Empty, (acc, s) => acc.Combine(s.Tokens));
        var todayCost = todaySlices.Sum(s => s.ComputedCostUsd);

        // Rolling window consumption
        var windowTokens = slices.Aggregate(TokenLedger.Empty, (acc, s) => acc.Combine(s.Tokens));
        var windowCost = slices.Sum(s => s.ComputedCostUsd);

        return new ConsumptionDigest
        {
            TodayTokens = todayTokens,
            TodayCostUsd = todayCost,
            RollingWindowTokens = windowTokens,
            RollingWindowCostUsd = windowCost,
            RollingWindowDays = windowDays,
            DailyBreakdown = slices,
            ComputedAt = DateTimeOffset.UtcNow
        };
    }
}
