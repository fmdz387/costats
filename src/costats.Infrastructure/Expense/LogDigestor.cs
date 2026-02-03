using System.Text.Json;
using costats.Core.Pulse;

namespace costats.Infrastructure.Expense;

/// <summary>
/// Parses JSONL log files and extracts token consumption data.
/// </summary>
public static class LogDigestor
{
    private const int MaxLineLength = 512 * 1024;

    /// <summary>
    /// Digests Claude Code log files and produces consumption slices.
    /// </summary>
    public static async Task<IReadOnlyList<ConsumptionSlice>> DigestClaudeLogsAsync(
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default)
    {
        var logDir = GetClaudeLogDirectory();
        if (!Directory.Exists(logDir))
            return [];

        var slices = new List<ConsumptionSlice>();
        var dedupeSet = new HashSet<string>();

        // Scan all project directories recursively (includes subagents subdirectories)
        foreach (var projectDir in Directory.EnumerateDirectories(logDir))
        {
            await ScanClaudeDirectoryRecursiveAsync(projectDir, since, until, slices, dedupeSet, cancellationToken);
        }

        return AggregateByDayAndModel(slices);
    }

    private static async Task ScanClaudeDirectoryRecursiveAsync(
        string directory,
        DateOnly since,
        DateOnly until,
        List<ConsumptionSlice> slices,
        HashSet<string> dedupeSet,
        CancellationToken cancellationToken)
    {
        // Scan jsonl files in current directory
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            await DigestClaudeFileAsync(file, since, until, slices, dedupeSet, cancellationToken);
        }

        // Recurse into subdirectories (e.g., subagents/)
        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            await ScanClaudeDirectoryRecursiveAsync(subDir, since, until, slices, dedupeSet, cancellationToken);
        }
    }

    /// <summary>
    /// Digests Codex log files and produces consumption slices.
    /// </summary>
    public static async Task<IReadOnlyList<ConsumptionSlice>> DigestCodexLogsAsync(
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default)
    {
        var logDir = GetCodexLogDirectory();
        if (!Directory.Exists(logDir))
            return [];

        var slices = new List<ConsumptionSlice>();

        foreach (var file in EnumerateCodexSessionFiles(logDir, since, until))
        {
            // Each file has its own cumulative totals - don't share across files
            await DigestCodexFileAsync(file, since, until, slices, cancellationToken);
        }

        return AggregateByDayAndModel(slices);
    }

    private static async Task DigestClaudeFileAsync(
        string filePath,
        DateOnly since,
        DateOnly until,
        List<ConsumptionSlice> slices,
        HashSet<string> dedupeSet,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var line in ReadLinesAsync(filePath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength)
                    continue;

                // Quick pre-filter before parsing JSON
                if (!line.Contains("\"type\":\"assistant\"") || !line.Contains("\"usage\""))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!TryGetString(root, "type", out var type) || type != "assistant")
                        continue;

                    if (!TryGetString(root, "timestamp", out var timestamp))
                        continue;

                    var entryDate = ParseDateFromTimestamp(timestamp);
                    if (entryDate is null || entryDate < since || entryDate > until)
                        continue;

                    if (!root.TryGetProperty("message", out var message))
                        continue;

                    if (!TryGetString(message, "model", out var model))
                        continue;

                    if (!message.TryGetProperty("usage", out var usage))
                        continue;

                    // Deduplicate by message ID + request ID (streaming sends duplicates)
                    var messageId = TryGetString(message, "id", out var mid) ? mid : null;
                    var requestId = TryGetString(root, "requestId", out var rid) ? rid : null;
                    if (messageId is not null && requestId is not null)
                    {
                        var dedupeKey = $"{messageId}:{requestId}";
                        if (!dedupeSet.Add(dedupeKey))
                            continue;
                    }

                    var ledger = ExtractClaudeLedger(usage);
                    if (ledger.TotalConsumed == 0)
                        continue;

                    var rate = TariffRegistry.FindClaudeRate(model);
                    var cost = rate.ComputeCost(ledger);

                    slices.Add(new ConsumptionSlice
                    {
                        Period = entryDate.Value,
                        ModelIdentifier = model,
                        Tokens = ledger,
                        ComputedCostUsd = cost
                    });
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }
        }
        catch (IOException)
        {
            // File access error, skip
        }
    }

    private static async Task DigestCodexFileAsync(
        string filePath,
        DateOnly since,
        DateOnly until,
        List<ConsumptionSlice> slices,
        CancellationToken cancellationToken)
    {
        string? currentModel = null;
        // Track cumulative totals per file (each file is a session)
        int prevInput = 0, prevCached = 0, prevOutput = 0;

        try
        {
            await foreach (var line in ReadLinesAsync(filePath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line) || line.Length > MaxLineLength)
                    continue;

                // Quick pre-filter
                var hasEventMsg = line.Contains("\"type\":\"event_msg\"");
                var hasTurnContext = line.Contains("\"type\":\"turn_context\"");

                if (!hasEventMsg && !hasTurnContext)
                    continue;

                if (hasEventMsg && !line.Contains("\"token_count\""))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (!TryGetString(root, "type", out var type))
                        continue;

                    // Extract model from turn context
                    if (type == "turn_context")
                    {
                        if (root.TryGetProperty("payload", out var turnPayload))
                        {
                            if (TryGetString(turnPayload, "model", out var m))
                                currentModel = m;
                            else if (turnPayload.TryGetProperty("info", out var turnInfo) && TryGetString(turnInfo, "model", out var m2))
                                currentModel = m2;
                        }
                        continue;
                    }

                    if (type != "event_msg")
                        continue;

                    if (!TryGetString(root, "timestamp", out var timestamp))
                        continue;

                    var entryDate = ParseDateFromTimestamp(timestamp);
                    if (entryDate is null || entryDate < since || entryDate > until)
                        continue;

                    if (!root.TryGetProperty("payload", out var eventPayload))
                        continue;

                    if (!TryGetString(eventPayload, "type", out var payloadType) || payloadType != "token_count")
                        continue;

                    // Get info object - may be nested under payload.info or directly in payload
                    JsonElement info = default;
                    if (eventPayload.TryGetProperty("info", out var infoEl) && infoEl.ValueKind == JsonValueKind.Object)
                        info = infoEl;
                    else
                        info = eventPayload;

                    var model = TryGetString(info, "model", out var mod) ? mod
                        : TryGetString(info, "model_name", out var mod2) ? mod2
                        : currentModel ?? "gpt-5";

                    // Extract token counts - prefer last_token_usage for incremental, fall back to delta from totals
                    int deltaInput = 0, deltaCached = 0, deltaOutput = 0;

                    if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                    {
                        // Use incremental values directly
                        deltaInput = Math.Max(0, GetIntOrZero(last, "input_tokens"));
                        deltaCached = GetIntOrZero(last, "cached_input_tokens");
                        if (deltaCached == 0) deltaCached = GetIntOrZero(last, "cache_read_input_tokens");
                        deltaOutput = Math.Max(0, GetIntOrZero(last, "output_tokens"));
                    }
                    else if (info.TryGetProperty("total_token_usage", out var totals) && totals.ValueKind == JsonValueKind.Object)
                    {
                        // Calculate delta from cumulative totals
                        var currInput = GetIntOrZero(totals, "input_tokens");
                        var currCached = GetIntOrZero(totals, "cached_input_tokens");
                        if (currCached == 0) currCached = GetIntOrZero(totals, "cache_read_input_tokens");
                        var currOutput = GetIntOrZero(totals, "output_tokens");

                        deltaInput = Math.Max(0, currInput - prevInput);
                        deltaCached = Math.Max(0, currCached - prevCached);
                        deltaOutput = Math.Max(0, currOutput - prevOutput);

                        prevInput = currInput;
                        prevCached = currCached;
                        prevOutput = currOutput;
                    }
                    else
                    {
                        continue;
                    }

                    if (deltaInput == 0 && deltaCached == 0 && deltaOutput == 0)
                        continue;

                    // Cached cannot exceed input
                    deltaCached = Math.Min(deltaCached, deltaInput);

                    var ledger = new TokenLedger
                    {
                        StandardInput = Math.Max(0, deltaInput - deltaCached),
                        CachedInput = Math.Max(0, deltaCached),
                        CacheWriteInput = 0,
                        GeneratedOutput = Math.Max(0, deltaOutput)
                    };

                    var rate = TariffRegistry.FindCodexRate(model);
                    var cost = rate.ComputeCost(ledger);

                    slices.Add(new ConsumptionSlice
                    {
                        Period = entryDate.Value,
                        ModelIdentifier = model,
                        Tokens = ledger,
                        ComputedCostUsd = cost
                    });
                }
                catch (JsonException)
                {
                    // Skip malformed lines
                }
            }
        }
        catch (IOException)
        {
            // File access error, skip
        }
    }

    private static TokenLedger ExtractClaudeLedger(JsonElement usage)
    {
        var input = GetIntOrZero(usage, "input_tokens");
        var cacheRead = GetIntOrZero(usage, "cache_read_input_tokens");
        var cacheCreate = GetIntOrZero(usage, "cache_creation_input_tokens");
        var output = GetIntOrZero(usage, "output_tokens");

        return new TokenLedger
        {
            StandardInput = Math.Max(0, input),
            CachedInput = Math.Max(0, cacheRead),
            CacheWriteInput = Math.Max(0, cacheCreate),
            GeneratedOutput = Math.Max(0, output)
        };
    }

    private static IReadOnlyList<ConsumptionSlice> AggregateByDayAndModel(List<ConsumptionSlice> rawSlices)
    {
        var grouped = rawSlices
            .GroupBy(s => (s.Period, s.ModelIdentifier))
            .Select(g =>
            {
                var combined = g.Aggregate(TokenLedger.Empty, (acc, s) => acc.Combine(s.Tokens));
                var totalCost = g.Sum(s => s.ComputedCostUsd);
                return new ConsumptionSlice
                {
                    Period = g.Key.Period,
                    ModelIdentifier = g.Key.ModelIdentifier,
                    Tokens = combined,
                    ComputedCostUsd = totalCost
                };
            })
            .OrderByDescending(s => s.Period)
            .ThenBy(s => s.ModelIdentifier)
            .ToList();

        return grouped;
    }

    private static string GetClaudeLogDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".claude", "projects");
    }

    private static string GetCodexLogDirectory()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
            return Path.Combine(codexHome.Trim(), "sessions");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".codex", "sessions");
    }

    private static IEnumerable<string> EnumerateCodexSessionFiles(string baseDir, DateOnly since, DateOnly until)
    {
        if (!Directory.Exists(baseDir))
            yield break;

        // Codex stores sessions in nested date directories: sessions/YYYY/MM/DD/*.jsonl
        for (var date = since; date <= until; date = date.AddDays(1))
        {
            var dayPath = Path.Combine(
                baseDir,
                date.Year.ToString("D4"),
                date.Month.ToString("D2"),
                date.Day.ToString("D2"));

            if (!Directory.Exists(dayPath))
                continue;

            foreach (var file in Directory.EnumerateFiles(dayPath, "*.jsonl"))
            {
                yield return file;
            }
        }
    }

    private static DateOnly? ParseDateFromTimestamp(string timestamp)
    {
        // Fast path: ISO 8601 format "YYYY-MM-DDTHH:MM:SS..."
        if (timestamp.Length >= 10 &&
            timestamp[4] == '-' &&
            timestamp[7] == '-')
        {
            if (int.TryParse(timestamp.AsSpan(0, 4), out var year) &&
                int.TryParse(timestamp.AsSpan(5, 2), out var month) &&
                int.TryParse(timestamp.AsSpan(8, 2), out var day))
            {
                try
                {
                    return new DateOnly(year, month, day);
                }
                catch
                {
                    // Invalid date components
                }
            }
        }

        // Fallback to full parse
        if (DateTimeOffset.TryParse(timestamp, out var dto))
            return DateOnly.FromDateTime(dto.LocalDateTime);

        return null;
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            value = prop.GetString() ?? string.Empty;
            return !string.IsNullOrEmpty(value);
        }
        value = string.Empty;
        return false;
    }

    private static int GetIntOrZero(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.GetInt32();
        return 0;
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (cancellationToken.IsCancellationRequested)
                yield break;
            yield return line;
        }
    }
}
