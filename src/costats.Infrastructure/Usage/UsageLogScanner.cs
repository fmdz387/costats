using System.Globalization;
using System.Text;
using System.Text.Json;

namespace costats.Infrastructure.Usage;

internal sealed class UsageLogScanner
{
    private readonly TimeSpan _sessionWindow = TimeSpan.FromHours(5);
    private readonly TimeSpan _weeklyWindow = TimeSpan.FromDays(7);
    private const int MaxLineLength = 512 * 1024; // 512 KB cap per line
    private const int FileReadBufferSize = 16 * 1024;
    private const int MaxDedupeKeyCount = 200_000;

    public Task<UsageLogResult> ScanCodexAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ScanCodexCore(cancellationToken), cancellationToken);
    }

    public Task<UsageLogResult> ScanClaudeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() => ScanClaudeCore(cancellationToken), cancellationToken);
    }

    private UsageLogResult ScanCodexCore(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionCutoff = now - _sessionWindow;
        var weekCutoff = now - _weeklyWindow;

        var sessionRecent = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var lastTotalsBySession = new Dictionary<string, TokenTotals>(StringComparer.OrdinalIgnoreCase);

        long sessionTokens = 0;
        long weekTokens = 0;
        DateTimeOffset? latest = null;
        string? latestSessionId = null;

        foreach (var file in EnumerateCodexFiles(weekCutoff))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite,
                Options = FileOptions.SequentialScan,
                BufferSize = FileReadBufferSize
            });
            using var reader = new StreamReader(stream);
            string? line;
            string? activeSessionId = null;
            var lineBuffer = new StringBuilder();

            while ((line = ReadLineLimited(reader, lineBuffer)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0)
                {
                    continue;
                }

                if (!line.Contains("\"type\""))
                {
                    continue;
                }

                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeElement))
                    {
                        continue;
                    }

                    var type = typeElement.GetString();
                    if (string.IsNullOrWhiteSpace(type))
                    {
                        continue;
                    }

                    if (type.Equals("session_meta", StringComparison.OrdinalIgnoreCase))
                    {
                        activeSessionId = TryGetSessionId(root);
                        continue;
                    }

                    if (!type.Equals("event_msg", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryGetTimestamp(root, out var timestamp))
                    {
                        continue;
                    }

                    var payload = root.TryGetProperty("payload", out var payloadElement)
                        ? payloadElement
                        : default;
                    if (payload.ValueKind == JsonValueKind.Undefined || payload.ValueKind == JsonValueKind.Null)
                    {
                        continue;
                    }

                    if (!payload.TryGetProperty("type", out var payloadType) ||
                        !string.Equals(payloadType.GetString(), "token_count", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var sessionId = TryGetSessionId(root) ?? activeSessionId ?? "unknown";
                    var tokens = ExtractCodexTokens(payload, lastTotalsBySession, sessionId);
                    if (tokens.Total == 0)
                    {
                        continue;
                    }

                    if (timestamp >= weekCutoff)
                    {
                        weekTokens += tokens.Total;
                    }

                    if (timestamp >= sessionCutoff)
                    {
                        sessionTokens += tokens.Total;
                    }

                    if (latest is null || timestamp > latest)
                    {
                        latest = timestamp;
                        latestSessionId = sessionId;
                    }

                    sessionRecent[sessionId] = timestamp;
                }
                catch (JsonException)
                {
                    continue;
                }
                finally
                {
                    doc?.Dispose();
                }
            }
        }

        // Estimate session start from first session token event
        DateTimeOffset? sessionStart = null;
        if (sessionTokens > 0 && latestSessionId is not null && sessionRecent.TryGetValue(latestSessionId, out var recentTs))
        {
            sessionStart = recentTs.AddMinutes(-30); // Rough estimate
        }

        return new UsageLogResult(
            sessionTokens,
            weekTokens,
            latest,
            sessionStart,
            latestSessionId);
    }

    private UsageLogResult ScanClaudeCore(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var sessionCutoff = now - _sessionWindow;
        var weekCutoff = now - _weeklyWindow;

        long sessionTokens = 0;
        long weekTokens = 0;
        DateTimeOffset? latest = null;
        DateTimeOffset? sessionStart = null;
        var seenKeys = new HashSet<MessageRequestKey>(MessageRequestKeyComparer.Instance);

        foreach (var file in EnumerateClaudeFiles(weekCutoff))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new FileStream(file, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite,
                Options = FileOptions.SequentialScan,
                BufferSize = FileReadBufferSize
            });
            using var reader = new StreamReader(stream);
            string? line;
            var lineBuffer = new StringBuilder();

            while ((line = ReadLineLimited(reader, lineBuffer)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0 || !line.Contains("\"type\""))
                {
                    continue;
                }

                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("type", out var typeElement))
                    {
                        continue;
                    }

                    var type = typeElement.GetString();
                    if (!string.Equals(type, "assistant", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryGetTimestamp(root, out var timestamp))
                    {
                        continue;
                    }

                    if (!root.TryGetProperty("message", out var messageElement))
                    {
                        continue;
                    }

                    if (!messageElement.TryGetProperty("usage", out var usageElement) ||
                        usageElement.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var messageId = messageElement.TryGetProperty("id", out var messageIdElement)
                        ? messageIdElement.GetString()
                        : null;
                    var requestId = root.TryGetProperty("requestId", out var requestIdElement)
                        ? requestIdElement.GetString()
                        : null;
                    if (!string.IsNullOrWhiteSpace(messageId) && !string.IsNullOrWhiteSpace(requestId))
                    {
                        if (!TryAddDedupeKey(seenKeys, new MessageRequestKey(messageId, requestId)))
                        {
                            continue;
                        }
                    }

                    var tokens = ExtractClaudeTokens(usageElement);
                    if (tokens == 0)
                    {
                        continue;
                    }

                    if (timestamp >= weekCutoff)
                    {
                        weekTokens += tokens;
                    }

                    if (timestamp >= sessionCutoff)
                    {
                        sessionTokens += tokens;
                        if (sessionStart is null || timestamp < sessionStart)
                        {
                            sessionStart = timestamp;
                        }
                    }

                    if (latest is null || timestamp > latest)
                    {
                        latest = timestamp;
                    }
                }
                catch (JsonException)
                {
                    continue;
                }
                finally
                {
                    doc?.Dispose();
                }
            }
        }

        return new UsageLogResult(sessionTokens, weekTokens, latest, sessionStart, null);
    }

    private static IEnumerable<string> EnumerateCodexFiles(DateTimeOffset oldestRelevant)
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(env))
        {
            roots.Add(Path.Combine(env.Trim(), "sessions"));
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".codex", "sessions"));
        }

        var archived = roots
            .Where(root => root.EndsWith("sessions", StringComparison.OrdinalIgnoreCase))
            .Select(root => Path.Combine(Path.GetDirectoryName(root) ?? root, "archived_sessions"))
            .ToList();

        roots.AddRange(archived);

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(file) < (oldestRelevant - TimeSpan.FromDays(1)).UtcDateTime)
                    continue;
                yield return file;
            }
        }
    }

    private static IEnumerable<string> EnumerateClaudeFiles(DateTimeOffset oldestRelevant)
    {
        var roots = new List<string>();
        var env = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        if (!string.IsNullOrWhiteSpace(env))
        {
            foreach (var part in env.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var basePath = part.Trim();
                if (string.IsNullOrWhiteSpace(basePath))
                {
                    continue;
                }

                if (Path.GetFileName(basePath).Equals("projects", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(basePath);
                }
                else
                {
                    roots.Add(Path.Combine(basePath, "projects"));
                }
            }
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            roots.Add(Path.Combine(home, ".config", "claude", "projects"));
            roots.Add(Path.Combine(home, ".claude", "projects"));
        }

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                if (File.GetLastWriteTimeUtc(file) < (oldestRelevant - TimeSpan.FromDays(1)).UtcDateTime)
                    continue;
                yield return file;
            }
        }
    }

    private static string? ReadLineLimited(StreamReader reader, StringBuilder buffer)
    {
        buffer.Clear();
        bool overflowed = false;
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

        // EOF reached
        if (buffer.Length == 0 && !overflowed)
            return null;

        return overflowed ? string.Empty : buffer.ToString();
    }

    private static TokenTotals ExtractCodexTokens(
        JsonElement payload,
        Dictionary<string, TokenTotals> lastTotalsBySession,
        string sessionId)
    {
        if (!payload.TryGetProperty("info", out var infoElement) ||
            infoElement.ValueKind != JsonValueKind.Object)
        {
            return TokenTotals.Empty;
        }

        if (infoElement.TryGetProperty("total_token_usage", out var totalElement))
        {
            var totals = new TokenTotals(
                input: GetInt(totalElement, "input_tokens"),
                cached: GetInt(totalElement, "cached_input_tokens", "cache_read_input_tokens"),
                output: GetInt(totalElement, "output_tokens"));

            if (lastTotalsBySession.TryGetValue(sessionId, out var previous))
            {
                lastTotalsBySession[sessionId] = totals;
                return totals.Delta(previous);
            }

            lastTotalsBySession[sessionId] = totals;
            return totals;
        }

        if (infoElement.TryGetProperty("last_token_usage", out var lastElement))
        {
            return new TokenTotals(
                input: GetInt(lastElement, "input_tokens"),
                cached: GetInt(lastElement, "cached_input_tokens", "cache_read_input_tokens"),
                output: GetInt(lastElement, "output_tokens"));
        }

        return TokenTotals.Empty;
    }

    private static long ExtractClaudeTokens(JsonElement usageElement)
    {
        var input = GetInt(usageElement, "input_tokens");
        var cacheCreate = GetInt(usageElement, "cache_creation_input_tokens");
        var cacheRead = GetInt(usageElement, "cache_read_input_tokens");
        var output = GetInt(usageElement, "output_tokens");
        return input + cacheCreate + cacheRead + output;
    }

    private static int GetInt(JsonElement element, string primary, string? secondary = null)
    {
        if (element.TryGetProperty(primary, out var primaryElement) &&
            primaryElement.TryGetInt32(out var primaryValue))
        {
            return primaryValue;
        }

        if (secondary is not null &&
            element.TryGetProperty(secondary, out var secondaryElement) &&
            secondaryElement.TryGetInt32(out var secondaryValue))
        {
            return secondaryValue;
        }

        return 0;
    }

    private static bool TryGetTimestamp(JsonElement element, out DateTimeOffset timestamp)
    {
        timestamp = default;
        if (!element.TryGetProperty("timestamp", out var tsElement))
        {
            return false;
        }

        var text = tsElement.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out timestamp);
    }

    private static string? TryGetSessionId(JsonElement element)
    {
        if (element.TryGetProperty("payload", out var payload) &&
            payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("session_id", out var sessionIdElement) &&
            sessionIdElement.GetString() is { Length: > 0 } sessionId)
        {
            return sessionId;
        }

        if (element.TryGetProperty("session_id", out var rootSessionId) &&
            rootSessionId.GetString() is { Length: > 0 } rootId)
        {
            return rootId;
        }

        return null;
    }

    private readonly struct TokenTotals
    {
        public static TokenTotals Empty => new(0, 0, 0);

        public TokenTotals(int input, int cached, int output)
        {
            Input = input;
            Cached = cached;
            Output = output;
        }

        public int Input { get; }
        public int Cached { get; }
        public int Output { get; }
        public long Total => (long)Input + Cached + Output;

        public TokenTotals Delta(TokenTotals previous)
        {
            var input = Math.Max(0, Input - previous.Input);
            var cached = Math.Max(0, Cached - previous.Cached);
            var output = Math.Max(0, Output - previous.Output);
            return new TokenTotals(input, cached, output);
        }

        public TokenTotals Add(TokenTotals other)
        {
            return new TokenTotals(Input + other.Input, Cached + other.Cached, Output + other.Output);
        }
    }

    private readonly record struct MessageRequestKey(string MessageId, string RequestId);

    private static bool TryAddDedupeKey(HashSet<MessageRequestKey> dedupeSet, MessageRequestKey key)
    {
        // Bound memory for very large log histories.
        if (dedupeSet.Count >= MaxDedupeKeyCount)
        {
            dedupeSet.Clear();
        }

        return dedupeSet.Add(key);
    }

    private sealed class MessageRequestKeyComparer : IEqualityComparer<MessageRequestKey>
    {
        public static MessageRequestKeyComparer Instance { get; } = new();

        public bool Equals(MessageRequestKey x, MessageRequestKey y)
        {
            return string.Equals(x.MessageId, y.MessageId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.RequestId, y.RequestId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(MessageRequestKey obj)
        {
            return HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MessageId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.RequestId));
        }
    }
}

internal readonly record struct UsageLogResult(
    long SessionTokens,
    long WeekTokens,
    DateTimeOffset? LatestTimestamp,
    DateTimeOffset? SessionStart,
    string? LatestSessionId);
