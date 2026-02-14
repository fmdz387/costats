using costats.Application.Pulse;
using costats.Core.Pulse;
using costats.Infrastructure.Expense;
using costats.Infrastructure.Usage;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

/// <summary>
/// An <see cref="ISignalSource"/> for a single multicc profile.
/// Identical logic to <see cref="ClaudeLogSource"/> but reads from the profile's configDir.
/// </summary>
public sealed class MulticcClaudeLogSource : ISignalSource, IDisposable
{
    private static readonly TimeSpan SessionDuration = TimeSpan.FromHours(5);
    private static readonly TimeSpan WeekDuration = TimeSpan.FromDays(7);

    private readonly MulticcProfile _profile;
    private readonly UsageLogScanner _scanner = new();
    private readonly ClaudeOAuthUsageFetcher _oauthFetcher;
    private readonly ExpenseAnalyzer _expenseAnalyzer = new();
    private readonly string _logDirectory;

    public MulticcClaudeLogSource(MulticcProfile profile)
    {
        _profile = profile;
        _oauthFetcher = new ClaudeOAuthUsageFetcher(profile.ConfigDir);
        _logDirectory = Path.Combine(profile.ConfigDir, "projects");
    }

    public ProviderProfile Profile => new(
        $"claude:{_profile.Name}",
        _profile.Name,
        "#FF7A00");

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // OAuth is a network call - run in parallel with file I/O
        var oauthTask = _oauthFetcher.FetchAsync(cancellationToken);

        // Log scan and expense analysis both read the same files - run sequentially
        var logResult = await _scanner.ScanClaudeAsync(_profile.ConfigDir, cancellationToken).ConfigureAwait(false);
        var consumption = await SafeAnalyzeExpenseAsync(cancellationToken).ConfigureAwait(false);

        var oauthResult = await oauthTask.ConfigureAwait(false);

        if (oauthResult is null && logResult.SessionTokens == 0 && logResult.WeekTokens == 0)
        {
            return new ProviderReading(
                Usage: null,
                Identity: null,
                StatusSummary: $"No data for {_profile.Name}",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.LocalLog);
        }

        // Prefer OAuth data for percentages
        var sessionUsedPercent = oauthResult?.FiveHourUsedPercent;
        var weeklyUsedPercent = oauthResult?.SevenDayUsedPercent;

        var sessionResetsAt = oauthResult?.FiveHourResetsAt ?? CalculateSessionReset(logResult.SessionStart, now);
        var weeklyResetsAt = oauthResult?.SevenDayResetsAt ?? CalculateWeeklyReset(now);

        var sessionWindow = new QuotaWindow(SessionDuration, sessionResetsAt);
        var weekWindow = new QuotaWindow(WeekDuration, weeklyResetsAt);

        long? sessionUsed;
        long? sessionLimit;
        long? weekUsed;
        long? weekLimit;

        if (sessionUsedPercent is not null)
        {
            sessionUsed = (long)Math.Round(sessionUsedPercent.Value);
            sessionLimit = 100;
        }
        else
        {
            sessionUsed = logResult.SessionTokens > 0 ? logResult.SessionTokens : null;
            sessionLimit = null;
        }

        if (weeklyUsedPercent is not null)
        {
            weekUsed = (long)Math.Round(weeklyUsedPercent.Value);
            weekLimit = 100;
        }
        else
        {
            weekUsed = logResult.WeekTokens > 0 ? logResult.WeekTokens : null;
            weekLimit = null;
        }

        // Build overage spending bucket when available
        MonetaryBucket? spendingBucket = null;
        if (oauthResult is { OverageEnabled: true, OverageSpentUsd: not null, OverageCeilingUsd: not null })
        {
            spendingBucket = MonetaryBucket.ForOverageSpend(
                (decimal)oauthResult.OverageSpentUsd.Value,
                (decimal)oauthResult.OverageCeilingUsd.Value);
        }

        var usage = new UsagePulse(
            ProviderId: Profile.ProviderId,
            CapturedAt: oauthResult?.FetchedAt ?? logResult.LatestTimestamp ?? now,
            SessionUsed: sessionUsed,
            SessionLimit: sessionLimit,
            WeekUsed: weekUsed,
            WeekLimit: weekLimit,
            SpendingBucket: spendingBucket,
            Consumption: consumption,
            SessionWindow: sessionWindow,
            WeekWindow: weekWindow);

        var planText = FormatPlanText(oauthResult?.SubscriptionType);
        var statusSummary = oauthResult is not null
            ? $"Updated {FormatRelativeTime(oauthResult.FetchedAt, now)}"
            : $"Updated {FormatRelativeTime(logResult.LatestTimestamp ?? now, now)}";

        var confidence = oauthResult is not null ? ReadingConfidence.High : ReadingConfidence.Medium;
        var source = oauthResult is not null ? ReadingSource.Api : ReadingSource.LocalLog;

        return new ProviderReading(
            Usage: usage,
            Identity: new IdentityCard(Profile.ProviderId, _profile.Name, null, null, planText, "OAuth"),
            StatusSummary: statusSummary,
            CapturedAt: usage.CapturedAt,
            Confidence: confidence,
            Source: source);
    }

    private static string FormatPlanText(string? subscriptionType)
    {
        if (string.IsNullOrEmpty(subscriptionType))
        {
            return "Max";
        }

        return char.ToUpper(subscriptionType[0]) + subscriptionType[1..].ToLower();
    }

    private static DateTimeOffset? CalculateSessionReset(DateTimeOffset? sessionStart, DateTimeOffset now)
    {
        if (sessionStart is null)
        {
            return now + SessionDuration;
        }

        var elapsed = now - sessionStart.Value;
        if (elapsed >= SessionDuration)
        {
            return now + SessionDuration;
        }

        return sessionStart.Value + SessionDuration;
    }

    private static DateTimeOffset CalculateWeeklyReset(DateTimeOffset now)
    {
        var daysUntilMonday = ((int)DayOfWeek.Monday - (int)now.DayOfWeek + 7) % 7;
        if (daysUntilMonday == 0 && now.TimeOfDay > TimeSpan.Zero)
        {
            daysUntilMonday = 7;
        }

        var nextMonday = now.Date.AddDays(daysUntilMonday);
        return new DateTimeOffset(nextMonday, TimeSpan.Zero);
    }

    private async Task<ConsumptionDigest?> SafeAnalyzeExpenseAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _expenseAnalyzer.AnalyzeClaudeAsync(_logDirectory, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cost analysis failure should not break usage display
            return null;
        }
    }

    public void Dispose()
    {
        _oauthFetcher.Dispose();
    }
}
