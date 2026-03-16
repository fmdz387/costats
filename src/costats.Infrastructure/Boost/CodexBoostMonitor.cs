using System.Text.Json;
using costats.Application.Boost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Boost;

/// <summary>
/// Polls iscodex2x.com/json once at startup then hourly to check whether the
/// Codex 2x promotional period is still active.
/// Unlike Claude's 2x promo, Codex 2x is around-the-clock (no peak-hour windows).
/// </summary>
public sealed class CodexBoostMonitor : BackgroundService, ICodexBoostMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    private readonly HttpClient _httpClient;
    private readonly ILogger<CodexBoostMonitor> _logger;

    public CodexBoostState? Current { get; private set; }
    public event EventHandler? StatusChanged;

    public CodexBoostMonitor(ILogger<CodexBoostMonitor> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://iscodex2x.com"),
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

            // "answer" is "YES" when the promo is active
            bool promoActive = root.TryGetProperty("answer", out var answer)
                && answer.GetString()?.Equals("YES", StringComparison.OrdinalIgnoreCase) == true;

            string deadline = root.TryGetProperty("deadline", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString() ?? string.Empty
                : string.Empty;

            Current = new CodexBoostState(promoActive, deadline, DateTimeOffset.UtcNow);
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "CodexBoost status fetch failed (non-critical)");
        }
    }

    public override void Dispose()
    {
        _httpClient.Dispose();
        base.Dispose();
    }
}
