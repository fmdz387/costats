using costats.Application.Pricing;
using costats.Core.Pulse;

namespace costats.Infrastructure.Expense;

internal static class ConsumptionSliceAggregator
{
    public static void Add(
        Dictionary<DateOnly, Dictionary<string, SliceAccumulator>> aggregates,
        DateOnly period,
        string modelIdentifier,
        TokenLedger ledger,
        decimal cost)
    {
        if (!aggregates.TryGetValue(period, out var byModel))
        {
            byModel = new Dictionary<string, SliceAccumulator>(StringComparer.OrdinalIgnoreCase);
            aggregates[period] = byModel;
        }

        byModel.TryGetValue(modelIdentifier, out var accumulator);
        accumulator.StandardInput += ledger.StandardInput;
        accumulator.CachedInput += ledger.CachedInput;
        accumulator.CacheWriteInput += ledger.CacheWriteInput;
        accumulator.GeneratedOutput += ledger.GeneratedOutput;
        accumulator.ReasoningOutput += ledger.ReasoningOutput;
        accumulator.Cost += cost;
        byModel[modelIdentifier] = accumulator;
    }

    public static IReadOnlyList<ConsumptionSlice> Build(
        Dictionary<DateOnly, Dictionary<string, SliceAccumulator>> aggregates) =>
        aggregates
            .SelectMany(day => day.Value.Select(model => new ConsumptionSlice
            {
                Period = day.Key,
                ModelIdentifier = model.Key,
                Tokens = model.Value.ToTokenLedger(),
                ComputedCostUsd = model.Value.Cost
            }))
            .OrderByDescending(slice => slice.Period)
            .ThenBy(slice => slice.ModelIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

internal record struct SliceAccumulator
{
    public long StandardInput { get; set; }
    public long CachedInput { get; set; }
    public long CacheWriteInput { get; set; }
    public long GeneratedOutput { get; set; }
    public long ReasoningOutput { get; set; }
    public decimal Cost { get; set; }

    public TokenLedger ToTokenLedger() => new()
    {
        StandardInput = Clamp(StandardInput),
        CachedInput = Clamp(CachedInput),
        CacheWriteInput = Clamp(CacheWriteInput),
        GeneratedOutput = Clamp(GeneratedOutput),
        ReasoningOutput = Clamp(ReasoningOutput)
    };

    private static int Clamp(long value)
    {
        if (value > int.MaxValue) return int.MaxValue;
        if (value < int.MinValue) return int.MinValue;
        return (int)value;
    }
}
