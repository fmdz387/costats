namespace costats.Core.Pulse;

public sealed record QuotaWindow(TimeSpan Duration, DateTimeOffset? ResetsAt);
