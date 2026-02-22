using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Providers;

public sealed class CopilotUsageFetcher : IDisposable
{
    private const string BaseUrl = "https://api.github.com/";
    private static readonly string[] UsagePaths =
    [
        "user/copilot/usage",
        "copilot/usage"
    ];
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
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("costats");
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.copilot-preview+json"));
        _httpClient.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<CopilotUsageFetchResult> FetchAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return CopilotUsageFetchResult.MissingToken();
        }

        var trimmedToken = token.Trim();

        foreach (var path in UsagePaths)
        {
            var result = await FetchFromPathAsync(path, trimmedToken, cancellationToken).ConfigureAwait(false);
            if (result.Status == CopilotFetchStatus.NotFound)
            {
                continue;
            }

            return result;
        }

        return CopilotUsageFetchResult.NotFound();
    }

    private async Task<CopilotUsageFetchResult> FetchFromPathAsync(string path, string token, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

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

    private static CopilotUsagePayload? ParsePayload(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var now = DateTimeOffset.UtcNow;
            var today = DateOnly.FromDateTime(now.UtcDateTime);
            var weekStart = today.AddDays(-6);

            long? todayAccepted = null;
            long? todaySuggested = null;
            long? weekAccepted = null;
            long? weekSuggested = null;

            var daily = ExtractDailyUsage(root);
            foreach (var day in daily)
            {
                if (day.Date == today)
                {
                    todayAccepted = SumValues(todayAccepted, day.Accepted);
                    todaySuggested = SumValues(todaySuggested, day.Suggested);
                }

                if (day.Date >= weekStart && day.Date <= today)
                {
                    weekAccepted = SumValues(weekAccepted, day.Accepted);
                    weekSuggested = SumValues(weekSuggested, day.Suggested);
                }
            }

            if (root.TryGetProperty("today", out var todayElement) && todayElement.ValueKind == JsonValueKind.Object)
            {
                todayAccepted ??= ReadLong(todayElement, AcceptedFields);
                todaySuggested ??= ReadLong(todayElement, SuggestedFields);
            }

            if (root.TryGetProperty("week", out var weekElement) && weekElement.ValueKind == JsonValueKind.Object)
            {
                weekAccepted ??= ReadLong(weekElement, AcceptedFields);
                weekSuggested ??= ReadLong(weekElement, SuggestedFields);
            }

            if (root.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.Object)
            {
                todayAccepted ??= ReadLong(summaryElement, "today_accepted", "today_lines_accepted", "today_acceptances");
                todaySuggested ??= ReadLong(summaryElement, "today_suggested", "today_lines_suggested", "today_suggestions");
                weekAccepted ??= ReadLong(summaryElement, "week_accepted", "week_lines_accepted", "week_acceptances");
                weekSuggested ??= ReadLong(summaryElement, "week_suggested", "week_lines_suggested", "week_suggestions");
            }

            var plan = ReadString(root, "plan", "subscription", "copilot_plan", "plan_type");
            var login = ReadString(root, "login", "username");

            if (root.TryGetProperty("user", out var userElement) && userElement.ValueKind == JsonValueKind.Object)
            {
                login ??= ReadString(userElement, "login", "username");
            }

            return new CopilotUsagePayload(
                todayAccepted,
                todaySuggested,
                weekAccepted,
                weekSuggested,
                plan,
                login,
                now);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<CopilotUsageDay> ExtractDailyUsage(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return ParseDailyArray(root);
        }

        foreach (var name in DailyArrayFields)
        {
            if (!root.TryGetProperty(name, out var element))
            {
                continue;
            }

            if (element.ValueKind == JsonValueKind.Array)
            {
                return ParseDailyArray(element);
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (var nestedName in DailyArrayFields)
                {
                    if (element.TryGetProperty(nestedName, out var nested) && nested.ValueKind == JsonValueKind.Array)
                    {
                        return ParseDailyArray(nested);
                    }
                }
            }
        }

        return Array.Empty<CopilotUsageDay>();
    }

    private static IReadOnlyList<CopilotUsageDay> ParseDailyArray(JsonElement array)
    {
        var results = new List<CopilotUsageDay>();

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var date = ReadDate(item);
            if (date is null)
            {
                continue;
            }

            var accepted = ReadLong(item, AcceptedFields);
            var suggested = ReadLong(item, SuggestedFields);
            results.Add(new CopilotUsageDay(date.Value, accepted, suggested));
        }

        return results;
    }

    private static DateOnly? ReadDate(JsonElement element)
    {
        foreach (var name in DateFields)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (DateOnly.TryParse(text, out var day))
                {
                    return day;
                }

                if (DateTimeOffset.TryParse(text, out var timestamp))
                {
                    return DateOnly.FromDateTime(timestamp.UtcDateTime);
                }
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            {
                var unix = numeric > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(numeric) : DateTimeOffset.FromUnixTimeSeconds(numeric);
                return DateOnly.FromDateTime(unix.UtcDateTime);
            }
        }

        return null;
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

    private static long? SumValues(long? first, long? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        return first.Value + second.Value;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private static readonly string[] DateFields =
    [
        "date",
        "day",
        "timestamp",
        "bucket"
    ];

    private static readonly string[] DailyArrayFields =
    [
        "days",
        "daily",
        "usage",
        "data"
    ];

    private static readonly string[] AcceptedFields =
    [
        "total_lines_accepted",
        "lines_accepted",
        "accepted_lines",
        "total_acceptances",
        "acceptances",
        "accepted_suggestions",
        "accepted_count",
        "acceptances_count",
        "total_accepted",
        "accepted"
    ];

    private static readonly string[] SuggestedFields =
    [
        "total_lines_suggested",
        "lines_suggested",
        "suggested_lines",
        "total_suggestions",
        "suggestions",
        "suggestions_count",
        "total_suggestions_count",
        "suggested_count",
        "total_suggested",
        "suggested"
    ];
}

public sealed record CopilotUsagePayload(
    long? TodayAccepted,
    long? TodaySuggested,
    long? WeekAccepted,
    long? WeekSuggested,
    string? Plan,
    string? Login,
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

public sealed record CopilotUsageDay(DateOnly Date, long? Accepted, long? Suggested);
