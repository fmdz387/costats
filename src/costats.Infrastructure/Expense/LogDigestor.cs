using System.Text;
using System.Text.Json;
using costats.Application.Pricing;
using costats.Core.Pulse;

namespace costats.Infrastructure.Expense;

/// <summary>
/// Parses JSONL log files and extracts token consumption data.
/// </summary>
public static class LogDigestor
{
    private const int MaxLineLength = 512 * 1024;
    private const int FileReadBufferSize = 16 * 1024;
    private const int MaxDedupeKeyCount = 250_000;

    /// <summary>
    /// Digests Claude Code log files and produces consumption slices.
    /// </summary>
    public static Task<IReadOnlyList<ConsumptionSlice>> DigestClaudeLogsAsync(
        IPricingCatalog pricingCatalog,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default) =>
        DigestClaudeLogsAsync(pricingCatalog, GetClaudeLogDirectory(), since, until, cancellationToken);

    /// <summary>
    /// Digests Claude Code log files from a specific directory and produces consumption slices.
    /// </summary>
    public static Task<IReadOnlyList<ConsumptionSlice>> DigestClaudeLogsAsync(
        IPricingCatalog pricingCatalog,
        string logDirectory,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => DigestClaudeLogsCoreAsync(pricingCatalog, logDirectory, since, until, cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<ConsumptionSlice>> DigestClaudeLogsCoreAsync(
        IPricingCatalog pricingCatalog,
        string logDirectory,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logDirectory))
            return [];

        var aggregates = new Dictionary<DateOnly, Dictionary<string, SliceAccumulator>>();
        var dedupeSet = new HashSet<MessageRequestKey>();
        var cutoff = since.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - TimeSpan.FromDays(1);

        foreach (var projectDir in Directory.EnumerateDirectories(logDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScanClaudeDirectoryRecursiveAsync(
                pricingCatalog,
                projectDir,
                since,
                until,
                cutoff,
                aggregates,
                dedupeSet,
                cancellationToken).ConfigureAwait(false);
        }

        return ConsumptionSliceAggregator.Build(aggregates);
    }

    private static async Task ScanClaudeDirectoryRecursiveAsync(
        IPricingCatalog pricingCatalog,
        string directory,
        DateOnly since,
        DateOnly until,
        DateTime cutoff,
        Dictionary<DateOnly, Dictionary<string, SliceAccumulator>> aggregates,
        HashSet<MessageRequestKey> dedupeSet,
        CancellationToken cancellationToken)
    {
        // Scan jsonl files in current directory
        foreach (var file in Directory.EnumerateFiles(directory, "*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.GetLastWriteTimeUtc(file) < cutoff)
                continue;

            await DigestClaudeFileAsync(
                pricingCatalog,
                file,
                since,
                until,
                aggregates,
                dedupeSet,
                cancellationToken).ConfigureAwait(false);
        }

        // Recurse into subdirectories (e.g., subagents/)
        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ScanClaudeDirectoryRecursiveAsync(
                pricingCatalog,
                subDir,
                since,
                until,
                cutoff,
                aggregates,
                dedupeSet,
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Digests Codex log files and produces consumption slices.
    /// </summary>
    public static Task<IReadOnlyList<ConsumptionSlice>> DigestCodexLogsAsync(
        IPricingCatalog pricingCatalog,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default) =>
        DigestCodexLogsAsync(pricingCatalog, GetCodexLogDirectory(), since, until, cancellationToken);

    public static Task<IReadOnlyList<ConsumptionSlice>> DigestCodexLogsAsync(
        IPricingCatalog pricingCatalog,
        string logDirectory,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => DigestCodexLogsCoreAsync(pricingCatalog, logDirectory, since, until, cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<ConsumptionSlice>> DigestCodexLogsCoreAsync(
        IPricingCatalog pricingCatalog,
        string logDirectory,
        DateOnly since,
        DateOnly until,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(logDirectory))
            return [];

        var aggregates = new Dictionary<DateOnly, Dictionary<string, SliceAccumulator>>();

        foreach (var file in EnumerateCodexSessionFiles(logDirectory, since, until))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DigestCodexFileAsync(pricingCatalog, file, since, until, aggregates, cancellationToken).ConfigureAwait(false);
        }

        return ConsumptionSliceAggregator.Build(aggregates);
    }

    private static async Task DigestClaudeFileAsync(
        IPricingCatalog pricingCatalog,
        string filePath,
        DateOnly since,
        DateOnly until,
        Dictionary<DateOnly, Dictionary<string, SliceAccumulator>> aggregates,
        HashSet<MessageRequestKey> dedupeSet,
        CancellationToken cancellationToken)
    {
        try
        {
            foreach (var line in ReadLines(filePath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
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
                        if (!TryAddDedupeKey(dedupeSet, new MessageRequestKey(messageId, requestId)))
                            continue;
                    }

                    var ledger = ExtractClaudeLedger(usage);
                    if (ledger.TotalConsumed == 0)
                        continue;

                    var pricing = await pricingCatalog.LookupAsync(model, "anthropic", cancellationToken).ConfigureAwait(false);
                    var cost = pricing is null ? 0m : PricingCostCalculator.ComputeCost(pricing, ledger);

                    ConsumptionSliceAggregator.Add(aggregates, entryDate.Value, model, ledger, cost);
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
        IPricingCatalog pricingCatalog,
        string filePath,
        DateOnly since,
        DateOnly until,
        Dictionary<DateOnly, Dictionary<string, SliceAccumulator>> aggregates,
        CancellationToken cancellationToken)
    {
        string? currentModel = null;
        // Track cumulative totals per file (each file is a session)
        int prevInput = 0, prevCached = 0, prevOutput = 0, prevReasoning = 0;

        try
        {
            foreach (var line in ReadLines(filePath, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(line))
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
                    int deltaInput = 0, deltaCached = 0, deltaOutput = 0, deltaReasoning = 0;

                    if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
                    {
                        // Use incremental values directly
                        deltaInput = Math.Max(0, GetIntOrZero(last, "input_tokens"));
                        deltaCached = GetIntOrZero(last, "cached_input_tokens");
                        if (deltaCached == 0) deltaCached = GetIntOrZero(last, "cache_read_input_tokens");
                        deltaOutput = Math.Max(0, GetIntOrZero(last, "output_tokens"));
                        deltaReasoning = Math.Max(0, GetReasoningOutputTokens(last));
                    }
                    else if (info.TryGetProperty("total_token_usage", out var totals) && totals.ValueKind == JsonValueKind.Object)
                    {
                        // Calculate delta from cumulative totals
                        var currInput = GetIntOrZero(totals, "input_tokens");
                        var currCached = GetIntOrZero(totals, "cached_input_tokens");
                        if (currCached == 0) currCached = GetIntOrZero(totals, "cache_read_input_tokens");
                        var currOutput = GetIntOrZero(totals, "output_tokens");
                        var currReasoning = GetReasoningOutputTokens(totals);

                        deltaInput = Math.Max(0, currInput - prevInput);
                        deltaCached = Math.Max(0, currCached - prevCached);
                        deltaOutput = Math.Max(0, currOutput - prevOutput);
                        deltaReasoning = Math.Max(0, currReasoning - prevReasoning);

                        prevInput = currInput;
                        prevCached = currCached;
                        prevOutput = currOutput;
                        prevReasoning = currReasoning;
                    }
                    else
                    {
                        continue;
                    }

                    if (deltaInput == 0 && deltaCached == 0 && deltaOutput == 0 && deltaReasoning == 0)
                        continue;

                    // Cached cannot exceed input
                    deltaCached = Math.Min(deltaCached, deltaInput);
                    deltaReasoning = Math.Min(deltaReasoning, deltaOutput);

                    var ledger = new TokenLedger
                    {
                        StandardInput = Math.Max(0, deltaInput - deltaCached),
                        CachedInput = Math.Max(0, deltaCached),
                        CacheWriteInput = 0,
                        GeneratedOutput = Math.Max(0, deltaOutput - deltaReasoning),
                        ReasoningOutput = Math.Max(0, deltaReasoning)
                    };

                    var pricing = await pricingCatalog.LookupAsync(model, "openai", cancellationToken).ConfigureAwait(false);
                    var cost = pricing is null ? 0m : PricingCostCalculator.ComputeCost(pricing, ledger);

                    ConsumptionSliceAggregator.Add(aggregates, entryDate.Value, model, ledger, cost);
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
        var reasoning = Math.Min(output, GetReasoningOutputTokens(usage));

        return new TokenLedger
        {
            StandardInput = Math.Max(0, input),
            CachedInput = Math.Max(0, cacheRead),
            CacheWriteInput = Math.Max(0, cacheCreate),
            GeneratedOutput = Math.Max(0, output - reasoning),
            ReasoningOutput = Math.Max(0, reasoning)
        };
    }

    private static IEnumerable<string> ReadLines(string filePath, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(filePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite,
            Options = FileOptions.SequentialScan,
            BufferSize = FileReadBufferSize
        });
        using var reader = new StreamReader(stream);
        var buffer = new StringBuilder();

        string? line;
        while ((line = ReadLineLimited(reader, buffer)) is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0)
            {
                continue;
            }

            yield return line;
        }
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

    private static int GetReasoningOutputTokens(JsonElement element)
    {
        var direct = GetIntOrZero(element, "reasoning_output_tokens");
        if (direct > 0)
        {
            return direct;
        }

        direct = GetIntOrZero(element, "reasoning_tokens");
        if (direct > 0)
        {
            return direct;
        }

        if (element.TryGetProperty("output_tokens_details", out var outputDetails) &&
            outputDetails.ValueKind == JsonValueKind.Object)
        {
            direct = GetIntOrZero(outputDetails, "reasoning_tokens");
            if (direct > 0)
            {
                return direct;
            }
        }

        if (element.TryGetProperty("completion_tokens_details", out var completionDetails) &&
            completionDetails.ValueKind == JsonValueKind.Object)
        {
            return GetIntOrZero(completionDetails, "reasoning_tokens");
        }

        return 0;
    }

    private static string? ReadLineLimited(StreamReader reader, StringBuilder buffer)
    {
        buffer.Clear();
        var overflowed = false;
        int ch;

        while ((ch = reader.Read()) != -1)
        {
            if (ch == '\r')
            {
                if (reader.Peek() == '\n')
                    reader.Read();
                return overflowed ? string.Empty : buffer.ToString();
            }

            if (ch == '\n')
            {
                return overflowed ? string.Empty : buffer.ToString();
            }

            if (!overflowed)
            {
                if (buffer.Length < MaxLineLength)
                {
                    buffer.Append((char)ch);
                }
                else
                {
                    overflowed = true;
                }
            }
        }

        if (buffer.Length == 0 && !overflowed)
            return null;

        return overflowed ? string.Empty : buffer.ToString();
    }

    private static bool TryAddDedupeKey(HashSet<MessageRequestKey> dedupeSet, MessageRequestKey key)
    {
        // Bound memory for very large log histories.
        if (dedupeSet.Count >= MaxDedupeKeyCount)
        {
            dedupeSet.Clear();
        }

        return dedupeSet.Add(key);
    }

    private readonly record struct MessageRequestKey(string MessageId, string RequestId);
}
