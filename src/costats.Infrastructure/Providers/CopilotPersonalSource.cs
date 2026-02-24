using costats.Application.Pulse;
using costats.Application.Security;
using costats.Application.Settings;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging;
using static costats.Core.Pulse.UsageFormatter;

namespace costats.Infrastructure.Providers;

public sealed class CopilotPersonalSource : ISignalSource
{
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

            var premium = result.Payload.Premium;
            var chat = result.Payload.Chat;
            var completions = result.Payload.Completions;

            // Pro/Business: premium_interactions is the primary quota, chat is secondary
            // Free plan: chat is the primary quota, completions is secondary
            // Pick the first non-null, non-unlimited quota for each slot
            var primaryQuota = PickQuota(premium, chat);
            var secondaryQuota = PickQuota(chat != primaryQuota ? chat : null, completions);

            long? sessionUsed = null;
            long? sessionLimit = null;
            if (primaryQuota is not null)
            {
                sessionUsed = primaryQuota.Used;
                sessionLimit = primaryQuota.Entitlement;
            }

            long? weekUsed = null;
            long? weekLimit = null;
            if (secondaryQuota is not null)
            {
                weekUsed = secondaryQuota.Used;
                weekLimit = secondaryQuota.Entitlement;
            }

            if (sessionUsed is null && weekUsed is null)
            {
                return new ProviderReading(
                    Usage: null,
                    Identity: identity,
                    StatusSummary: "No Copilot usage data available",
                    CapturedAt: now,
                    Confidence: ReadingConfidence.Low,
                    Source: ReadingSource.Api);
            }

            // Copilot quotas reset monthly (from quota_reset_date_utc)
            var resetAt = result.Payload.QuotaResetAt;
            var monthlyDuration = CalculateMonthlyDuration(now, resetAt);
            QuotaWindow? sessionWindow = sessionUsed is not null
                ? new QuotaWindow(monthlyDuration, resetAt)
                : null;
            QuotaWindow? weekWindow = weekUsed is not null
                ? new QuotaWindow(monthlyDuration, resetAt)
                : null;

            var usage = new UsagePulse(
                ProviderId: Profile.ProviderId,
                CapturedAt: result.Payload.FetchedAt,
                SessionUsed: sessionUsed,
                SessionLimit: sessionLimit,
                WeekUsed: weekUsed,
                WeekLimit: weekLimit,
                SpendingBucket: null,
                Consumption: null,
                SessionWindow: sessionWindow,
                WeekWindow: weekWindow);

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

    /// <summary>
    /// Returns the first non-null, non-unlimited quota from the candidates.
    /// </summary>
    private static CopilotQuotaSnapshot? PickQuota(params CopilotQuotaSnapshot?[] candidates)
    {
        foreach (var q in candidates)
        {
            if (q is not null && !q.Unlimited)
            {
                return q;
            }
        }

        return null;
    }

    /// <summary>
    /// Calculates the monthly window duration based on the reset date.
    /// Falls back to ~30 days if no reset date is available.
    /// </summary>
    private static TimeSpan CalculateMonthlyDuration(DateTimeOffset now, DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return TimeSpan.FromDays(30);
        }

        // Window start is approximately one month before reset
        var resetDate = resetAt.Value;
        var windowStart = resetDate.AddMonths(-1);
        return resetDate - windowStart;
    }

    private static string FormatPlanText(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
        {
            return "Copilot";
        }

        // Handle plans like "individual_pro" → "Individual Pro"
        return string.Join(' ', plan.Split('_')
            .Select(word => word.Length > 0
                ? char.ToUpper(word[0]) + word[1..].ToLower()
                : word));
    }
}
