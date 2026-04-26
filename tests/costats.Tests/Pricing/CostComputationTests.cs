using costats.Application.Pricing;
using costats.Core.Pulse;
using Xunit;

namespace costats.Tests.Pricing;

public sealed class CostComputationTests
{
    [Fact]
    public void ComputeCost_sums_all_buckets_independently()
    {
        var pricing = new ModelPricing
        {
            InputCostPerToken = 1m,
            OutputCostPerToken = 10m,
            ReasoningOutputCostPerToken = 20m,
            CacheReadInputTokenCost = 0.1m,
            CacheCreationInputTokenCost = 2m
        };
        var ledger = new TokenLedger
        {
            StandardInput = 3,
            CachedInput = 5,
            CacheWriteInput = 7,
            GeneratedOutput = 11,
            ReasoningOutput = 13
        };

        var cost = PricingCostCalculator.ComputeCost(pricing, ledger);

        Assert.Equal(387.5m, cost);
    }

    [Fact]
    public void ComputeCost_applies_200k_tier_per_bucket()
    {
        var pricing = new ModelPricing
        {
            InputCostPerToken = 1m,
            InputCostPerTokenAbove200k = 2m,
            OutputCostPerToken = 3m,
            OutputCostPerTokenAbove200k = 4m,
            ReasoningOutputCostPerToken = 9m,
            ReasoningOutputCostPerTokenAbove200k = 10m,
            CacheReadInputTokenCost = 5m,
            CacheReadInputTokenCostAbove200k = 6m,
            CacheCreationInputTokenCost = 7m,
            CacheCreationInputTokenCostAbove200k = 8m
        };
        var ledger = new TokenLedger
        {
            StandardInput = 200_001,
            GeneratedOutput = 200_001,
            ReasoningOutput = 200_001,
            CachedInput = 200_001,
            CacheWriteInput = 200_001
        };

        var cost = PricingCostCalculator.ComputeCost(pricing, ledger);

        Assert.Equal(5_000_030m, cost);
    }

    [Fact]
    public void ComputeCost_treats_null_and_negative_values_as_zero()
    {
        var pricing = new ModelPricing
        {
            InputCostPerToken = null,
            OutputCostPerToken = 1m
        };
        var ledger = new TokenLedger
        {
            StandardInput = 10,
            CachedInput = -10,
            CacheWriteInput = -10,
            GeneratedOutput = 2
        };

        var cost = PricingCostCalculator.ComputeCost(pricing, ledger);

        Assert.Equal(2m, cost);
    }

    [Fact]
    public void ComputeCost_prices_reasoning_as_output_when_no_specific_rate_exists()
    {
        var pricing = new ModelPricing
        {
            OutputCostPerToken = 7m
        };
        var ledger = new TokenLedger
        {
            StandardInput = 0,
            CachedInput = 0,
            CacheWriteInput = 0,
            GeneratedOutput = 2,
            ReasoningOutput = 3
        };

        var cost = PricingCostCalculator.ComputeCost(pricing, ledger);

        Assert.Equal(35m, cost);
    }

    [Fact]
    public void ComputeCost_allows_partial_pricing_data()
    {
        var pricing = new ModelPricing
        {
            InputCostPerToken = 2m
        };
        var ledger = new TokenLedger
        {
            StandardInput = 4,
            CachedInput = 6,
            CacheWriteInput = 8,
            GeneratedOutput = 10,
            ReasoningOutput = 12
        };

        var cost = PricingCostCalculator.ComputeCost(pricing, ledger);

        Assert.Equal(8m, cost);
    }
}
