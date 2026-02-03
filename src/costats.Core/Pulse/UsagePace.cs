namespace costats.Core.Pulse;

/// <summary>
/// Represents the pace of usage relative to the expected consumption rate.
/// </summary>
public sealed record UsagePace(
    PaceStage Stage,
    double DeltaPercent,
    double ExpectedUsedPercent,
    double ActualUsedPercent,
    TimeSpan? EtaUntilExhausted,
    bool WillLastToReset)
{
    /// <summary>
    /// Calculate pace for a usage window.
    /// </summary>
    /// <param name="usedPercent">The current usage as a percentage (0-100).</param>
    /// <param name="resetsAt">When the window resets.</param>
    /// <param name="windowDuration">The total window duration.</param>
    /// <param name="now">The current time.</param>
    public static UsagePace? Calculate(
        double usedPercent,
        DateTimeOffset? resetsAt,
        TimeSpan windowDuration,
        DateTimeOffset? now = null)
    {
        if (resetsAt is null || windowDuration <= TimeSpan.Zero)
        {
            return null;
        }

        var currentTime = now ?? DateTimeOffset.UtcNow;
        var timeUntilReset = resetsAt.Value - currentTime;
        if (timeUntilReset <= TimeSpan.Zero)
        {
            return null;
        }

        var elapsed = windowDuration - timeUntilReset;
        if (elapsed <= TimeSpan.Zero)
        {
            return null;
        }

        var elapsedRatio = elapsed.TotalSeconds / windowDuration.TotalSeconds;
        var expected = Math.Clamp(elapsedRatio * 100, 0, 100);
        var actual = Math.Clamp(usedPercent, 0, 100);

        // Negative delta = behind (good), Positive delta = ahead (burning through quota)
        var delta = actual - expected;
        var stage = GetStage(delta);

        // Calculate if we'll run out before reset
        TimeSpan? etaSeconds = null;
        var willLastToReset = false;

        if (elapsed > TimeSpan.Zero && actual > 0)
        {
            var rate = actual / elapsed.TotalSeconds; // % per second
            if (rate > 0)
            {
                var remaining = Math.Max(0, 100 - actual);
                var candidateSeconds = remaining / rate;
                if (candidateSeconds >= timeUntilReset.TotalSeconds)
                {
                    willLastToReset = true;
                }
                else
                {
                    etaSeconds = TimeSpan.FromSeconds(candidateSeconds);
                }
            }
        }
        else if (elapsed > TimeSpan.Zero && actual == 0)
        {
            willLastToReset = true;
        }

        return new UsagePace(stage, delta, expected, actual, etaSeconds, willLastToReset);
    }

    private static PaceStage GetStage(double delta)
    {
        var absDelta = Math.Abs(delta);
        if (absDelta <= 2) return PaceStage.OnTrack;
        if (absDelta <= 6) return delta >= 0 ? PaceStage.SlightlyAhead : PaceStage.SlightlyBehind;
        if (absDelta <= 12) return delta >= 0 ? PaceStage.Ahead : PaceStage.Behind;
        return delta >= 0 ? PaceStage.FarAhead : PaceStage.FarBehind;
    }
}

public enum PaceStage
{
    OnTrack,
    SlightlyAhead,
    Ahead,
    FarAhead,
    SlightlyBehind,
    Behind,
    FarBehind
}
