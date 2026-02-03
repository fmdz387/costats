using System.Globalization;

namespace costats.Core.Pulse;

/// <summary>
/// Formatting utilities for usage display.
/// </summary>
public static class UsageFormatter
{
    /// <summary>
    /// Format a countdown description like "in 3h 53m" or "in 2d 5h".
    /// </summary>
    public static string ResetCountdown(DateTimeOffset resetsAt, DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var seconds = Math.Max(0, (resetsAt - currentTime).TotalSeconds);
        if (seconds < 1) return "now";

        var totalMinutes = Math.Max(1, (int)Math.Ceiling(seconds / 60.0));
        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;

        if (days > 0)
        {
            if (hours > 0) return $"in {days}d {hours}h";
            return $"in {days}d";
        }
        if (hours > 0)
        {
            if (minutes > 0) return $"in {hours}h {minutes}m";
            return $"in {hours}h";
        }
        return $"in {totalMinutes}m";
    }

    /// <summary>
    /// Format pace information like "42% in reserve · Lasts until reset".
    /// </summary>
    public static string? FormatPace(UsagePace? pace, DateTimeOffset? now = null)
    {
        if (pace is null) return null;

        var left = FormatPaceLeft(pace);
        var right = FormatPaceRight(pace, now);

        if (right is not null)
        {
            return $"Pace: {left} · {right}";
        }
        return $"Pace: {left}";
    }

    private static string FormatPaceLeft(UsagePace pace)
    {
        var deltaValue = (int)Math.Round(Math.Abs(pace.DeltaPercent));
        return pace.Stage switch
        {
            PaceStage.OnTrack => "On pace",
            PaceStage.SlightlyAhead or PaceStage.Ahead or PaceStage.FarAhead => $"{deltaValue}% in deficit",
            PaceStage.SlightlyBehind or PaceStage.Behind or PaceStage.FarBehind => $"{deltaValue}% in reserve",
            _ => "Unknown"
        };
    }

    private static string? FormatPaceRight(UsagePace pace, DateTimeOffset? now)
    {
        if (pace.WillLastToReset) return "Lasts until reset";
        if (pace.EtaUntilExhausted is null) return null;

        var eta = pace.EtaUntilExhausted.Value;
        var etaText = FormatDuration(eta);
        if (etaText == "now") return "Runs out now";
        return $"Runs out in {etaText}";
    }

    /// <summary>
    /// Format a duration like "2h 30m" or "1d 5h".
    /// </summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var totalMinutes = (int)Math.Ceiling(duration.TotalMinutes);
        if (totalMinutes < 1) return "now";

        var days = totalMinutes / (24 * 60);
        var hours = (totalMinutes / 60) % 24;
        var minutes = totalMinutes % 60;

        if (days > 0)
        {
            if (hours > 0) return $"{days}d {hours}h";
            return $"{days}d";
        }
        if (hours > 0)
        {
            if (minutes > 0) return $"{hours}h {minutes}m";
            return $"{hours}h";
        }
        return $"{totalMinutes}m";
    }

    /// <summary>
    /// Format a currency amount like "$ 0.04" or "$ 254.24".
    /// </summary>
    public static string FormatCurrency(decimal amount, string currencyCode = "USD")
    {
        return amount.ToString("C2", new CultureInfo("en-US"));
    }

    /// <summary>
    /// Format a token count like "15K" or "218M".
    /// </summary>
    public static string FormatTokenCount(long tokens)
    {
        return tokens switch
        {
            >= 1_000_000_000 => $"{tokens / 1_000_000_000.0:0.#}B",
            >= 1_000_000 => $"{tokens / 1_000_000.0:0.#}M",
            >= 1_000 => $"{tokens / 1_000.0:0.#}K",
            _ => tokens.ToString()
        };
    }

    /// <summary>
    /// Format usage percentage like "42% used".
    /// </summary>
    public static string FormatUsagePercent(double usedPercent)
    {
        return $"{(int)Math.Round(usedPercent)}% used";
    }

    /// <summary>
    /// Format a relative time like "Updated just now" or "Updated 5m ago".
    /// </summary>
    public static string FormatRelativeTime(DateTimeOffset timestamp, DateTimeOffset? now = null)
    {
        var currentTime = now ?? DateTimeOffset.UtcNow;
        var elapsed = currentTime - timestamp;

        if (elapsed.TotalSeconds < 30) return "just now";
        if (elapsed.TotalMinutes < 1) return "less than a minute ago";
        if (elapsed.TotalMinutes < 60) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed.TotalHours < 24) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }
}
