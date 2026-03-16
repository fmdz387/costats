using System.Text.Json;
using costats.Application.Boost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Boost;

/// <summary>
/// Polls isclaude2x.com/json every 2 minutes to track whether the Claude 2x
/// promotional limits are currently active and exposes the state to subscribers.
/// </summary>
public sealed class ClaudeBoostMonitor : BackgroundService, IClaudeBoostMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);

    private readonly HttpClient _httpClient;
    private readonly ILogger<ClaudeBoostMonitor> _logger;

    public ClaudeBoostState? Current { get; private set; }
    public event EventHandler? StatusChanged;

    public ClaudeBoostMonitor(ILogger<ClaudeBoostMonitor> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://isclaude2x.com"),
            Timeout = TimeSpan.FromSeconds(10)
        };
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "costats/1.0");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await FetchAsync(stoppingToken);
            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = await _httpClient.GetAsync("/json", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return;

            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            bool is2x = root.TryGetProperty("is2x", out var p1) && p1.GetBoolean();
            bool promoActive = root.TryGetProperty("promoActive", out var p2) && p2.GetBoolean();
            bool isPeak = root.TryGetProperty("isPeak", out var p3) && p3.GetBoolean();
            bool isWeekend = root.TryGetProperty("isWeekend", out var p4) && p4.GetBoolean();

            var expiresIn = string.Empty;
            if (is2x && root.TryGetProperty("2xWindowExpiresIn", out var p5) && p5.ValueKind == JsonValueKind.String)
                expiresIn = p5.GetString() ?? string.Empty;
            else if (!is2x && root.TryGetProperty("standardWindowExpiresIn", out var p6) && p6.ValueKind == JsonValueKind.String)
                expiresIn = p6.GetString() ?? string.Empty;

            Current = new ClaudeBoostState(is2x, promoActive, isPeak, isWeekend, expiresIn, DateTimeOffset.UtcNow);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "ClaudeBoost status fetch failed (non-critical)");
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
