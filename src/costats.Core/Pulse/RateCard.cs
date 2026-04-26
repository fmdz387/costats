namespace costats.Core.Pulse;

public static class RateCardMath
{
    private const int TierThreshold = 200_000;

    public static decimal ComputeTieredCost(int tokens, decimal? baseRate, decimal? aboveRate)
    {
        if (tokens <= 0)
        {
            return 0;
        }

        var baseCost = baseRate ?? 0m;
        if (tokens <= TierThreshold || aboveRate is null)
        {
            return tokens * baseCost;
        }

        return (TierThreshold * baseCost) + ((tokens - TierThreshold) * aboveRate.Value);
    }
}
