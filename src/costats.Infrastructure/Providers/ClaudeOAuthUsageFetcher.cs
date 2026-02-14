using System.Net.Http.Headers;
using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Fetches Claude usage data via the OAuth API.
/// This provides accurate utilization percentages directly from Anthropic.
/// </summary>
public sealed class ClaudeOAuthUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://api.anthropic.com";
    private const string UsagePath = "/api/oauth/usage";
    private const string BetaHeader = "oauth-2025-04-20";

    private readonly HttpClient _httpClient;
    private readonly string? _configDir;

    public ClaudeOAuthUsageFetcher()
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("anthropic-beta", BetaHeader);
    }

    public ClaudeOAuthUsageFetcher(string configDir) : this()
    {
        _configDir = configDir;
    }

    public async Task<ClaudeOAuthUsageResult?> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var credentials = await LoadCredentialsAsync(_configDir);
            if (credentials?.AccessToken is null)
            {
                return null;
            }

            // Check if token is expired
            if (credentials.ExpiresAt.HasValue && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() > credentials.ExpiresAt.Value)
            {
                return null; // Token expired, would need refresh
            }

            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credentials.AccessToken);

            var response = await _httpClient.GetAsync(UsagePath, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseResponse(content, credentials.SubscriptionType, credentials.RateLimitTier);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<ClaudeCredentials?> LoadCredentialsAsync(string? configDir)
    {
        string credentialsPath;
        if (configDir is not null)
        {
            credentialsPath = Path.Combine(configDir, ".credentials.json");
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            credentialsPath = Path.Combine(home, ".claude", ".credentials.json");
        }

        if (!File.Exists(credentialsPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(credentialsPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth))
            {
                return null;
            }

            return new ClaudeCredentials(
                oauth.TryGetProperty("accessToken", out var at) ? at.GetString() : null,
                oauth.TryGetProperty("refreshToken", out var rt) ? rt.GetString() : null,
                oauth.TryGetProperty("expiresAt", out var exp) ? exp.GetInt64() : null,
                oauth.TryGetProperty("subscriptionType", out var st) ? st.GetString() : null,
                oauth.TryGetProperty("rateLimitTier", out var rlt) ? rlt.GetString() : null);
        }
        catch
        {
            return null;
        }
    }

    private static ClaudeOAuthUsageResult? ParseResponse(string json, string? subscriptionType, string? rateLimitTier)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double? fiveHourPercent = null;
            DateTimeOffset? fiveHourResetsAt = null;
            double? sevenDayPercent = null;
            DateTimeOffset? sevenDayResetsAt = null;

            // Parse five_hour window
            if (root.TryGetProperty("five_hour", out var fiveHour))
            {
                if (fiveHour.TryGetProperty("utilization", out var util))
                {
                    fiveHourPercent = util.ValueKind == JsonValueKind.Number ? util.GetDouble() : null;
                }
                if (fiveHour.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(resets.GetString(), out var resetsTime))
                    {
                        fiveHourResetsAt = resetsTime;
                    }
                }
            }

            // Parse seven_day window
            if (root.TryGetProperty("seven_day", out var sevenDay))
            {
                if (sevenDay.TryGetProperty("utilization", out var util))
                {
                    sevenDayPercent = util.ValueKind == JsonValueKind.Number ? util.GetDouble() : null;
                }
                if (sevenDay.TryGetProperty("resets_at", out var resets) && resets.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(resets.GetString(), out var resetsTime))
                    {
                        sevenDayResetsAt = resetsTime;
                    }
                }
            }

            // Parse extra_usage (overage spending bucket)
            double? extraUsed = null;
            double? extraLimit = null;
            bool overageEnabled = false;
            if (root.TryGetProperty("extra_usage", out var extra))
            {
                if (extra.TryGetProperty("is_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.True)
                {
                    overageEnabled = true;
                }
                if (extra.TryGetProperty("used_credits", out var used) && used.ValueKind == JsonValueKind.Number)
                {
                    extraUsed = used.GetDouble();
                }
                if (extra.TryGetProperty("monthly_limit", out var limit) && limit.ValueKind == JsonValueKind.Number)
                {
                    extraLimit = limit.GetDouble();
                }

                // API returns minor currency units (cents) - convert to major units (dollars)
                // Some subscription tiers report inflated values; apply correction when ceiling exceeds plausible threshold
                if (extraUsed.HasValue && extraLimit.HasValue)
                {
                    (extraUsed, extraLimit) = NormalizeMonetaryValues(extraUsed.Value, extraLimit.Value, subscriptionType);
                }
            }

            return new ClaudeOAuthUsageResult(
                fiveHourPercent,
                fiveHourResetsAt,
                sevenDayPercent,
                sevenDayResetsAt,
                overageEnabled,
                extraUsed,
                extraLimit,
                subscriptionType,
                rateLimitTier,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts API monetary values from minor units and applies tier-specific corrections.
    /// </summary>
    private static (double used, double limit) NormalizeMonetaryValues(double rawUsed, double rawLimit, string? tier)
    {
        // Convert from cents to dollars
        var usedDollars = rawUsed / 100.0;
        var limitDollars = rawLimit / 100.0;

        // Enterprise tiers report accurate values; other tiers may have inflated figures
        // When ceiling exceeds reasonable threshold, apply additional correction factor
        const double PlausibilityThreshold = 500.0;
        var isEnterpriseTier = tier?.Contains("enterprise", StringComparison.OrdinalIgnoreCase) == true;

        if (!isEnterpriseTier && limitDollars > PlausibilityThreshold)
        {
            usedDollars /= 100.0;
            limitDollars /= 100.0;
        }

        return (usedDollars, limitDollars);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record ClaudeCredentials(
        string? AccessToken,
        string? RefreshToken,
        long? ExpiresAt,
        string? SubscriptionType,
        string? RateLimitTier);
}

public sealed record ClaudeOAuthUsageResult(
    double? FiveHourUsedPercent,
    DateTimeOffset? FiveHourResetsAt,
    double? SevenDayUsedPercent,
    DateTimeOffset? SevenDayResetsAt,
    bool OverageEnabled,
    double? OverageSpentUsd,
    double? OverageCeilingUsd,
    string? SubscriptionType,
    string? RateLimitTier,
    DateTimeOffset FetchedAt);
