using System.Globalization;
using System.Text.Json;
using costats.Application.Pricing;
using costats.Core.Pulse;

namespace costats.Infrastructure.Expense;

/// <summary>
/// Parses GitHub Copilot OTEL-shaped logs without assuming a single exporter schema.
/// </summary>
public static class CopilotOtelDigestor
{
    private static readonly string[] InputTokenKeys =
    [
        "gen_ai.usage.input_tokens",
        "llm.usage.prompt_tokens",
        "prompt_tokens",
        "input_tokens"
    ];

    private static readonly string[] CachedInputTokenKeys =
    [
        "gen_ai.usage.cached_input_tokens",
        "cached_input_tokens",
        "cache_read_input_tokens"
    ];

    private static readonly string[] OutputTokenKeys =
    [
        "gen_ai.usage.output_tokens",
        "llm.usage.completion_tokens",
        "completion_tokens",
        "output_tokens"
    ];

    private static readonly string[] ReasoningTokenKeys =
    [
        "gen_ai.usage.reasoning_output_tokens",
        "reasoning_output_tokens",
        "output_tokens_details.reasoning_tokens",
        "completion_tokens_details.reasoning_tokens",
        "reasoning_tokens"
    ];

    private static readonly string[] ModelKeys =
    [
        "gen_ai.response.model",
        "gen_ai.request.model",
        "model",
        "model_name",
        "ai.model"
    ];

    private static readonly string[] TimestampKeys =
    [
        "time_unix_nano",
        "timeUnixNano",
        "start_time_unix_nano",
        "startTimeUnixNano",
        "timestamp",
        "time"
    ];

    private static readonly string[] OtelValueProperties =
    [
        "stringValue",
        "intValue",
        "doubleValue",
        "boolValue"
    ];

    private static readonly string[] CandidateFileExtensions = [".jsonl", ".json", ".log"];

    public static async Task<IReadOnlyList<ConsumptionSlice>> DigestAsync(
        IPricingCatalog pricingCatalog,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default)
    {
        var roots = GetDefaultLogRoots().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return await DigestAsync(pricingCatalog, roots, since, until, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<IReadOnlyList<ConsumptionSlice>> DigestAsync(
        IPricingCatalog pricingCatalog,
        IEnumerable<string> roots,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default)
    {
        var aggregates = new Dictionary<DateOnly, Dictionary<string, SliceAccumulator>>();
        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in EnumerateCandidateFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await DigestFileAsync(pricingCatalog, file, since, until, aggregates, cancellationToken).ConfigureAwait(false);
            }
        }

        return ConsumptionSliceAggregator.Build(aggregates);
    }

    private static async Task DigestFileAsync(
        IPricingCatalog pricingCatalog,
        string file,
        DateOnly since,
        DateOnly until,
        Dictionary<DateOnly, Dictionary<string, SliceAccumulator>> aggregates,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line) ||
                    (!line.Contains("tokens", StringComparison.OrdinalIgnoreCase) &&
                     !line.Contains("gen_ai", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (!TryParseRecord(line, out var record) || record is null)
                {
                    continue;
                }

                var period = DateOnly.FromDateTime(record.Timestamp.LocalDateTime);
                if (period < since || period > until || record.Tokens.TotalConsumed == 0)
                {
                    continue;
                }

                var pricing = await pricingCatalog.LookupAsync(record.Model, null, cancellationToken).ConfigureAwait(false);
                var cost = pricing is null ? 0m : PricingCostCalculator.ComputeCost(pricing, record.Tokens);
                ConsumptionSliceAggregator.Add(aggregates, period, record.Model, record.Tokens, cost);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryParseRecord(string json, out CopilotOtelRecord? record)
    {
        record = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Flatten(doc.RootElement, fields);

            if (!TryFirst(fields, ModelKeys, out var model))
            {
                return false;
            }

            var timestamp = TryFirst(fields, TimestampKeys, out var rawTimestamp)
                ? ParseTimestamp(rawTimestamp)
                : DateTimeOffset.UtcNow;
            var input = GetInt(fields, InputTokenKeys);
            var cached = Math.Min(input, GetInt(fields, CachedInputTokenKeys));
            var output = GetInt(fields, OutputTokenKeys);
            var reasoning = Math.Min(output, GetInt(fields, ReasoningTokenKeys));

            record = new CopilotOtelRecord(
                model,
                timestamp,
                new TokenLedger
                {
                    StandardInput = Math.Max(0, input - cached),
                    CachedInput = Math.Max(0, cached),
                    CacheWriteInput = 0,
                    GeneratedOutput = Math.Max(0, output - reasoning),
                    ReasoningOutput = Math.Max(0, reasoning)
                });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void Flatten(JsonElement element, Dictionary<string, string> fields, string? prefix = null)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryReadOtelAttribute(element, fields))
                {
                    return;
                }

                foreach (var property in element.EnumerateObject())
                {
                    var key = prefix is null ? property.Name : $"{prefix}.{property.Name}";
                    Flatten(property.Value, fields, key);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(item, fields, prefix);
                }
                break;
            case JsonValueKind.String:
                if (prefix is not null)
                {
                    fields[prefix] = element.GetString() ?? string.Empty;
                }
                break;
            case JsonValueKind.Number:
                if (prefix is not null)
                {
                    fields[prefix] = element.GetRawText();
                }
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                if (prefix is not null)
                {
                    fields[prefix] = element.GetBoolean().ToString(CultureInfo.InvariantCulture);
                }
                break;
        }
    }

    private static bool TryReadOtelAttribute(JsonElement element, Dictionary<string, string> fields)
    {
        if (!element.TryGetProperty("key", out var keyElement) ||
            keyElement.ValueKind != JsonValueKind.String ||
            !element.TryGetProperty("value", out var valueElement))
        {
            return false;
        }

        var key = keyElement.GetString();
        var value = ReadOtelValue(valueElement);
        if (!string.IsNullOrWhiteSpace(key) && value is not null)
        {
            fields[key] = value;
            return true;
        }

        return false;
    }

    private static string? ReadOtelValue(JsonElement valueElement)
    {
        if (valueElement.ValueKind != JsonValueKind.Object)
        {
            return valueElement.ValueKind == JsonValueKind.String ? valueElement.GetString() : valueElement.GetRawText();
        }

        foreach (var name in OtelValueProperties)
        {
            if (valueElement.TryGetProperty(name, out var prop))
            {
                return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.GetRawText();
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(file => CandidateFileExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));

    private static IEnumerable<string> GetDefaultLogRoots()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(appData, "Code", "User", "globalStorage", "github.copilot-chat");
        yield return Path.Combine(appData, "Code", "User", "globalStorage", "github.copilot");
        yield return Path.Combine(appData, "Cursor", "User", "globalStorage", "github.copilot-chat");
        yield return Path.Combine(appData, "Cursor", "User", "globalStorage", "github.copilot");
        yield return Path.Combine(localAppData, "GitHubCopilot");
        yield return Path.Combine(home, ".copilot");
    }

    private static bool TryFirst(IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> keys, out string value)
    {
        foreach (var key in keys)
        {
            if (fields.TryGetValue(key, out value!) && !string.IsNullOrWhiteSpace(value))
            {
                return true;
            }
        }

        foreach (var key in keys)
        {
            var suffix = $".{key}";
            foreach (var field in fields)
            {
                if (field.Key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(field.Value))
                {
                    value = field.Value;
                    return true;
                }
            }
        }

        value = string.Empty;
        return false;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> keys)
    {
        return TryFirst(fields, keys, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : 0;
    }

    private static DateTimeOffset ParseTimestamp(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            if (numeric > 10_000_000_000_000_000)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(numeric / 1_000_000);
            }

            if (numeric > 10_000_000_000)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(numeric);
            }

            return DateTimeOffset.FromUnixTimeSeconds(numeric);
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private sealed record CopilotOtelRecord(string Model, DateTimeOffset Timestamp, TokenLedger Tokens);
}
