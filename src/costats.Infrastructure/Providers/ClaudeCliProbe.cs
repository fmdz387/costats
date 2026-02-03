using System.Diagnostics;
using System.Text.RegularExpressions;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Probes Claude CLI for usage information by running 'claude /usage'.
/// Parses the rendered output to extract utilization percentages.
/// </summary>
public sealed class ClaudeCliProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<ClaudeUsageProbeResult?> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunClaudeUsageAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(output))
            {
                return null;
            }

            return ParseOutput(output);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static async Task<string?> RunClaudeUsageAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "claude",
            Arguments = "/usage",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var process = new Process { StartInfo = startInfo };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(Timeout);

        try
        {
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(cts.Token);

            await Task.WhenAll(outputTask, errorTask);

            // Wait for process to exit
            await process.WaitForExitAsync(cts.Token);

            return outputTask.Result;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            return null;
        }
    }

    private static ClaudeUsageProbeResult? ParseOutput(string output)
    {
        // Parse output looking for percentage patterns like:
        // "Current session: 10% (90% remaining)"
        // "Session usage: 10% used"
        // "Weekly: 15% used"
        // "7-day: 15%"

        double? sessionPercent = null;
        double? weeklyPercent = null;
        DateTimeOffset? sessionResetsAt = null;
        DateTimeOffset? weeklyResetsAt = null;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var lineLower = line.ToLowerInvariant();

            // Look for session-related lines
            if (lineLower.Contains("session") || lineLower.Contains("5 hour") || lineLower.Contains("5-hour") || lineLower.Contains("five hour"))
            {
                var percent = ExtractPercent(line);
                if (percent is not null)
                {
                    sessionPercent = ConvertToUsedPercent(percent.Value, line);
                }

                var resetTime = ExtractResetTime(line);
                if (resetTime is not null)
                {
                    sessionResetsAt = resetTime;
                }
            }
            // Look for weekly-related lines
            else if (lineLower.Contains("week") || lineLower.Contains("7 day") || lineLower.Contains("7-day") || lineLower.Contains("seven day"))
            {
                var percent = ExtractPercent(line);
                if (percent is not null)
                {
                    weeklyPercent = ConvertToUsedPercent(percent.Value, line);
                }

                var resetTime = ExtractResetTime(line);
                if (resetTime is not null)
                {
                    weeklyResetsAt = resetTime;
                }
            }
        }

        if (sessionPercent is null && weeklyPercent is null)
        {
            return null;
        }

        return new ClaudeUsageProbeResult(
            sessionPercent,
            weeklyPercent,
            sessionResetsAt,
            weeklyResetsAt,
            DateTimeOffset.UtcNow);
    }

    private static double? ExtractPercent(string line)
    {
        // Match patterns like "45%", "45.5%", "45 %"
        var match = Regex.Match(line, @"(\d{1,3}(?:\.\d+)?)\s*%");
        if (match.Success && double.TryParse(match.Groups[1].Value, out var percent))
        {
            return Math.Clamp(percent, 0, 100);
        }

        return null;
    }

    private static double ConvertToUsedPercent(double rawPercent, string context)
    {
        var contextLower = context.ToLowerInvariant();

        // If the context says "remaining" or "left", convert to used
        if (contextLower.Contains("remaining") || contextLower.Contains("left") || contextLower.Contains("available"))
        {
            return 100 - rawPercent;
        }

        // If the context says "used", it's already the used percent
        // Default assumption: the percentage shown is the used amount
        return rawPercent;
    }

    private static DateTimeOffset? ExtractResetTime(string line)
    {
        // Look for patterns like "resets in 3h 45m" or "resets at 2:30 PM"
        var matchIn = Regex.Match(line, @"resets?\s+in\s+(\d+)\s*h(?:ours?)?\s*(\d+)?\s*m?", RegexOptions.IgnoreCase);
        if (matchIn.Success)
        {
            var hours = int.Parse(matchIn.Groups[1].Value);
            var minutes = matchIn.Groups[2].Success ? int.Parse(matchIn.Groups[2].Value) : 0;
            return DateTimeOffset.UtcNow.AddHours(hours).AddMinutes(minutes);
        }

        // Pattern: "resets in Xd Yh"
        var matchDays = Regex.Match(line, @"resets?\s+in\s+(\d+)\s*d(?:ays?)?\s*(\d+)?\s*h?", RegexOptions.IgnoreCase);
        if (matchDays.Success)
        {
            var days = int.Parse(matchDays.Groups[1].Value);
            var hours = matchDays.Groups[2].Success ? int.Parse(matchDays.Groups[2].Value) : 0;
            return DateTimeOffset.UtcNow.AddDays(days).AddHours(hours);
        }

        return null;
    }
}

public sealed record ClaudeUsageProbeResult(
    double? SessionUsedPercent,
    double? WeeklyUsedPercent,
    DateTimeOffset? SessionResetsAt,
    DateTimeOffset? WeeklyResetsAt,
    DateTimeOffset ProbedAt);
