using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Providers;

public sealed class CopilotUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://api.github.com/";
    private const string CopilotUserAgent = "GitHubCopilotChat/0.26.7";
    private const string EditorVersion = "vscode/1.96.2";
    private const string EditorPluginVersion = "copilot-chat/0.26.7";
    private const string ApiVersion = "2025-04-01";
    private const string UsagePath = "copilot_internal/user";
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(800)
    ];

    private readonly HttpClient _httpClient;
    private readonly ILogger<CopilotUsageFetcher> _logger;

    public CopilotUsageFetcher(ILogger<CopilotUsageFetcher> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(CopilotUserAgent);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Add("Editor-Version", EditorVersion);
        _httpClient.DefaultRequestHeaders.Add("Editor-Plugin-Version", EditorPluginVersion);
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", ApiVersion);
    }

    public async Task<CopilotUsageFetchResult> FetchAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return CopilotUsageFetchResult.MissingToken();
        }

        var trimmedToken = token.Trim();

        return await FetchFromPathAsync(UsagePath, trimmedToken, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CopilotUsageFetchResult> FetchFromPathAsync(string path, string token, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("token", token);

                var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    return CopilotUsageFetchResult.InvalidToken();
                }

                if (response.StatusCode == HttpStatusCode.Forbidden)
                {
                    return CopilotUsageFetchResult.Forbidden();
                }

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return CopilotUsageFetchResult.NotFound();
                }

                if ((int)response.StatusCode == 429)
                {
                    return CopilotUsageFetchResult.RateLimited();
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Copilot usage request failed with status {StatusCode}", response.StatusCode);
                    return CopilotUsageFetchResult.Failed("Copilot usage request failed.");
                }

                var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var payload = ParsePayload(content);

                if (payload is null)
                {
                    return CopilotUsageFetchResult.Failed("Copilot usage response could not be parsed.");
                }

                return CopilotUsageFetchResult.Success(payload);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < RetryDelays.Length)
            {
                _logger.LogWarning(ex, "Copilot usage fetch attempt {Attempt} failed", attempt + 1);
            }

            if (attempt < RetryDelays.Length)
            {
                await Task.Delay(RetryDelays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }

        return CopilotUsageFetchResult.Failed("Copilot usage request failed.");
    }

    internal static CopilotUsagePayload? ParsePayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow;

            CopilotQuotaSnapshot? premium = null;
            CopilotQuotaSnapshot? chat = null;
            CopilotQuotaSnapshot? completions = null;

            // Pro/Business plans: quota_snapshots with detailed snapshot objects
            if (root.TryGetProperty("quota_snapshots", out var snapshots) && snapshots.ValueKind == JsonValueKind.Object)
            {
                premium = ParseQuotaSnapshot(snapshots, "premium_interactions");
                chat = ParseQuotaSnapshot(snapshots, "chat");
                completions = ParseQuotaSnapshot(snapshots, "completions");
            }

            // Free plan: limited_user_quotas (remaining) + monthly_quotas (total)
            if (premium is null && chat is null)
            {
                var hasLimited = root.TryGetProperty("limited_user_quotas", out var limited) && limited.ValueKind == JsonValueKind.Object;
                var hasMonthly = root.TryGetProperty("monthly_quotas", out var monthly) && monthly.ValueKind == JsonValueKind.Object;

                if (hasLimited && hasMonthly)
                {
                    chat = BuildFreeQuota(limited, monthly, "chat");
                    completions = BuildFreeQuota(limited, monthly, "completions");
                }
            }

            // Prefer quota_reset_date_utc (full ISO 8601) over quota_reset_date / limited_user_reset_date
            var quotaResetAt = ReadDateTime(root, "quota_reset_date_utc", "quota_reset_date", "limited_user_reset_date");

            var plan = ReadString(root, "copilot_plan");
            var login = ReadString(root, "login");

            return new CopilotUsagePayload(
                Premium: premium,
                Chat: chat,
                Completions: completions,
                Plan: plan,
                Login: login,
                QuotaResetAt: quotaResetAt,
                FetchedAt: now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static CopilotQuotaSnapshot? BuildFreeQuota(JsonElement limited, JsonElement monthly, string key)
    {
        var remaining = ReadLong(limited, key);
        var total = ReadLong(monthly, key);

        if (remaining is null || total is null || total <= 0)
        {
            return null;
        }

        var used = Math.Max(total.Value - remaining.Value, 0);
        var percentRemaining = (double)remaining.Value / total.Value * 100.0;

        return new CopilotQuotaSnapshot(
            Entitlement: total.Value,
            Remaining: remaining.Value,
            PercentRemaining: percentRemaining,
            Unlimited: false,
            OveragePermitted: false,
            OverageCount: 0);
    }

    private static CopilotQuotaSnapshot? ParseQuotaSnapshot(JsonElement snapshots, string key)
    {
        if (!snapshots.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entitlement = ReadLong(element, "entitlement");
        var remaining = ReadLong(element, "remaining");
        var percentRemaining = ReadDouble(element, "percent_remaining");
        var unlimited = ReadBool(element, "unlimited");
        var overagePermitted = ReadBool(element, "overage_permitted");
        var overageCount = ReadLong(element, "overage_count");

        return new CopilotQuotaSnapshot(
            Entitlement: entitlement ?? 0,
            Remaining: remaining ?? 0,
            PercentRemaining: percentRemaining,
            Unlimited: unlimited ?? false,
            OveragePermitted: overagePermitted ?? false,
            OverageCount: overageCount ?? 0);
    }

    private static long? ReadLong(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                return number;
            }

            if (value.ValueKind == JsonValueKind.String && double.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static bool? ReadBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }
        }

        return null;
    }

    private static DateTimeOffset? ReadDateTime(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (DateTimeOffset.TryParse(value.GetString(), out var timestamp))
            {
                return timestamp;
            }
        }

        return null;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

public sealed record CopilotQuotaSnapshot(
    long Entitlement,
    long Remaining,
    double? PercentRemaining,
    bool Unlimited,
    bool OveragePermitted,
    long OverageCount)
{
    public long Used => Math.Max(Entitlement - Remaining, 0);
}

public sealed record CopilotUsagePayload(
    CopilotQuotaSnapshot? Premium,
    CopilotQuotaSnapshot? Chat,
    CopilotQuotaSnapshot? Completions,
    string? Plan,
    string? Login,
    DateTimeOffset? QuotaResetAt,
    DateTimeOffset FetchedAt);

public enum CopilotFetchStatus
{
    Success = 0,
    MissingToken = 1,
    InvalidToken = 2,
    Forbidden = 3,
    NotFound = 4,
    RateLimited = 5,
    Failed = 6
}

public sealed record CopilotUsageFetchResult(
    CopilotFetchStatus Status,
    CopilotUsagePayload? Payload,
    string StatusSummary)
{
    public static CopilotUsageFetchResult Success(CopilotUsagePayload payload)
        => new(CopilotFetchStatus.Success, payload, "Copilot usage loaded.");

    public static CopilotUsageFetchResult MissingToken()
        => new(CopilotFetchStatus.MissingToken, null, "Copilot token not configured.");

    public static CopilotUsageFetchResult InvalidToken()
        => new(CopilotFetchStatus.InvalidToken, null, "Copilot token rejected.");

    public static CopilotUsageFetchResult Forbidden()
        => new(CopilotFetchStatus.Forbidden, null, "Copilot access denied for this token.");

    public static CopilotUsageFetchResult NotFound()
        => new(CopilotFetchStatus.NotFound, null, "Copilot usage endpoint unavailable or token lacks Copilot access.");

    public static CopilotUsageFetchResult RateLimited()
        => new(CopilotFetchStatus.RateLimited, null, "Copilot usage rate limited.");

    public static CopilotUsageFetchResult Failed(string message)
        => new(CopilotFetchStatus.Failed, null, message);
}
