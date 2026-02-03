using System.Diagnostics;
using System.Text.RegularExpressions;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Probes Codex CLI for usage information.
/// </summary>
public sealed class CodexCliProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<CodexUsageProbeResult?> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var output = await RunCodexStatusAsync(cancellationToken);
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

    private static async Task<string?> RunCodexStatusAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "codex",
            Arguments = "--status",
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
            await process.WaitForExitAsync(cts.Token);

            return outputTask.Result;
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(); } catch { }
            return null;
        }
    }

    private static CodexUsageProbeResult? ParseOutput(string output)
    {
        double? sessionPercent = null;
        double? weeklyPercent = null;
        DateTimeOffset? sessionResetsAt = null;
        DateTimeOffset? weeklyResetsAt = null;

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            var lineLower = line.ToLowerInvariant();

            if (lineLower.Contains("session") || lineLower.Contains("5 hour") || lineLower.Contains("5-hour"))
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
            else if (lineLower.Contains("week") || lineLower.Contains("7 day") || lineLower.Contains("7-day"))
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

        return new CodexUsageProbeResult(
            sessionPercent,
            weeklyPercent,
            sessionResetsAt,
            weeklyResetsAt,
            DateTimeOffset.UtcNow);
    }

    private static double? ExtractPercent(string line)
    {
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

        if (contextLower.Contains("remaining") || contextLower.Contains("left") || contextLower.Contains("available"))
        {
            return 100 - rawPercent;
        }

        return rawPercent;
    }

    private static DateTimeOffset? ExtractResetTime(string line)
    {
        var matchIn = Regex.Match(line, @"resets?\s+in\s+(\d+)\s*h(?:ours?)?\s*(\d+)?\s*m?", RegexOptions.IgnoreCase);
        if (matchIn.Success)
        {
            var hours = int.Parse(matchIn.Groups[1].Value);
            var minutes = matchIn.Groups[2].Success ? int.Parse(matchIn.Groups[2].Value) : 0;
            return DateTimeOffset.UtcNow.AddHours(hours).AddMinutes(minutes);
        }

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

public sealed record CodexUsageProbeResult(
    double? SessionUsedPercent,
    double? WeeklyUsedPercent,
    DateTimeOffset? SessionResetsAt,
    DateTimeOffset? WeeklyResetsAt,
    DateTimeOffset ProbedAt);
