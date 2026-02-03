namespace costats.Core.Models;

public sealed record RateWindow(TimeSpan Duration, DateTimeOffset? ResetsAt);
