using costats.Core.Pulse;

namespace costats.Application.Pricing;

public static class PricingCostCalculator
{
    public static decimal ComputeCost(ModelPricing pricing, TokenLedger ledger)
    {
        var reasoningRate = pricing.ReasoningOutputCostPerToken
            ?? pricing.ReasoningCostPerToken
            ?? pricing.OutputCostPerReasoningToken
            ?? pricing.OutputCostPerToken;
        var reasoningAboveRate = pricing.ReasoningOutputCostPerTokenAbove200k
            ?? pricing.OutputCostPerTokenAbove200k;

        return RateCardMath.ComputeTieredCost(ledger.StandardInput, pricing.InputCostPerToken, pricing.InputCostPerTokenAbove200k)
            + RateCardMath.ComputeTieredCost(ledger.GeneratedOutput, pricing.OutputCostPerToken, pricing.OutputCostPerTokenAbove200k)
            + RateCardMath.ComputeTieredCost(ledger.ReasoningOutput, reasoningRate, reasoningAboveRate)
            + RateCardMath.ComputeTieredCost(ledger.CachedInput, pricing.CacheReadInputTokenCost, pricing.CacheReadInputTokenCostAbove200k)
            + RateCardMath.ComputeTieredCost(ledger.CacheWriteInput, pricing.CacheCreationInputTokenCost, pricing.CacheCreationInputTokenCostAbove200k);
    }
}
