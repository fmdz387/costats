using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public sealed class CopilotPersonalSource : ISignalSource
{
    private static readonly TimeSpan DayDuration = TimeSpan.FromDays(1);
    private static readonly TimeSpan WeekDuration = TimeSpan.FromDays(7);

    private readonly AppSettings _settings;
    private readonly ICredentialVault _credentialVault;
    private readonly CopilotUsageFetcher _fetcher;
    private readonly ILogger<CopilotPersonalSource> _logger;

    public CopilotPersonalSource(
        AppSettings settings,
        ICredentialVault credentialVault,
        CopilotUsageFetcher fetcher,
        ILogger<CopilotPersonalSource> logger)
    {
        _settings = settings;
        _credentialVault = credentialVault;
        _fetcher = fetcher;
        _logger = logger;
    }

    public ProviderProfile Profile => ProviderCatalog.Copilot;

    public async Task<ProviderReading> ReadAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        if (!_settings.CopilotEnabled)
        {
            return new ProviderReading(
                Usage: null,
                Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, "Copilot", "Token"),
                StatusSummary: "Copilot disabled in Settings",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Api);
        }

        try
        {
            var token = await _credentialVault.LoadAsync(CredentialKeys.CopilotToken, cancellationToken).ConfigureAwait(false);
            var result = await _fetcher.FetchAsync(token, cancellationToken).ConfigureAwait(false);

            var identity = new IdentityCard(
                Profile.ProviderId,
                result.Payload?.Login ?? Profile.DisplayName,
                null,
                null,
                FormatPlanText(result.Payload?.Plan),
                "Token");

            if (result.Status != CopilotFetchStatus.Success || result.Payload is null)
            {
                return new ProviderReading(
                    Usage: null,
                    Identity: identity,
                    StatusSummary: result.StatusSummary,
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Api);
            }

            var sessionPercent = result.Payload.SessionUsedPercent
                ?? CalculatePercent(result.Payload.TodayAccepted, result.Payload.TodaySuggested);
            var weekPercent = result.Payload.WeekUsedPercent
                ?? CalculatePercent(result.Payload.WeekAccepted, result.Payload.WeekSuggested);

            if (sessionPercent is null && weekPercent is null)
            {
                return new ProviderReading(
                    Usage: null,
                    Identity: identity,
                    StatusSummary: "No Copilot usage data available",
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Api);
            }

            var resetAt = result.Payload.QuotaResetAt;
            DateTimeOffset? sessionResetsAt = sessionPercent is not null
                ? resetAt ?? CalculateNextDailyReset(now)
                : null;
            DateTimeOffset? weekResetsAt = weekPercent is not null
                ? resetAt ?? CalculateWeeklyReset(now)
                : null;

            var usage = new UsagePulse(
                ProviderId: Profile.ProviderId,
                CapturedAt: result.Payload.FetchedAt,
                SessionUsed: sessionPercent is not null ? (long)Math.Round(sessionPercent.Value) : null,
                SessionLimit: sessionPercent is not null ? 100 : null,
                WeekUsed: weekPercent is not null ? (long)Math.Round(weekPercent.Value) : null,
                WeekLimit: weekPercent is not null ? 100 : null,
                SpendingBucket: null,
                Consumption: null,
                SessionWindow: sessionResetsAt is not null ? new QuotaWindow(DayDuration, sessionResetsAt) : null,
                WeekWindow: weekResetsAt is not null ? new QuotaWindow(WeekDuration, weekResetsAt) : null);

            var statusSummary = $"Updated {FormatRelativeTime(result.Payload.FetchedAt, now)}";

            return new ProviderReading(
                Usage: usage,
                Identity: identity,
                StatusSummary: statusSummary,
                CapturedAt: usage.CapturedAt,
                Confidence: ReadingConfidence.Medium,
                Source: ReadingSource.Api);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Copilot usage read failed");
            return new ProviderReading(
                Usage: null,
                Identity: new IdentityCard(Profile.ProviderId, Profile.DisplayName, null, null, "Copilot", "Token"),
                StatusSummary: "Copilot usage unavailable",
                CapturedAt: now,
                Confidence: ReadingConfidence.Low,
                Source: ReadingSource.Api);
        }
    }

    private static double? CalculatePercent(long? accepted, long? suggested)
    {
        if (accepted is null || suggested is null || suggested <= 0)
        {
            return null;
        }

        var percent = Math.Clamp(accepted.Value * 100.0 / suggested.Value, 0, 100);
        return percent;
    }

    private static DateTimeOffset CalculateNextDailyReset(DateTimeOffset now)
    {
        var nextDay = now.Date.AddDays(1);
        return new DateTimeOffset(nextDay, TimeSpan.Zero);
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

    private static string FormatPlanText(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return "Copilot";
        }

        return char.ToUpper(plan[0]) + plan[1..].ToLower();
    }
}
