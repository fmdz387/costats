namespace costats.Core.Pulse;

/// <summary>
/// Represents a monetary spending bucket for overage or prepaid balances.
/// </summary>
public sealed record MonetaryBucket
{
    public required BucketKind Kind { get; init; }
    public required decimal Consumed { get; init; }
    public required decimal Ceiling { get; init; }
    public required string CurrencySymbol { get; init; }
    public DateTimeOffset? CycleEndsAt { get; init; }

    public decimal Available => Math.Max(0, Ceiling - Consumed);
    public double FillRatio => Ceiling > 0 ? Math.Clamp((double)(Consumed / Ceiling), 0, 1) : 0;

    public static MonetaryBucket ForOverageSpend(decimal spent, decimal cap, string currency = "$")
        => new()
        {
            Kind = BucketKind.OverageSpend,
            Consumed = spent,
            Ceiling = cap,
            CurrencySymbol = currency
        };

    public static MonetaryBucket ForPrepaidBalance(decimal remaining, string currency = "$")
        => new()
        {
            Kind = BucketKind.PrepaidBalance,
            Consumed = 0,
            Ceiling = remaining,
            CurrencySymbol = currency
        };
}

public enum BucketKind
{
    OverageSpend,
    PrepaidBalance
}
