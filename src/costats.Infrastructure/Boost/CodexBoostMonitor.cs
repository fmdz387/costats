using System.Text.RegularExpressions;
using costats.Application.Boost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Boost;

/// <summary>
/// Polls iscodex2x.com once at startup then hourly to check whether the
/// Codex 2x promotional period is still active.
/// The site has no JSON API — status and deadline are parsed from the HTML.
/// Unlike Claude's 2x promo, Codex 2x is around-the-clock (no peak-hour windows).
/// </summary>
public sealed class CodexBoostMonitor : BackgroundService, ICodexBoostMonitor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    // Matches data-promo-state="active" in the page HTML
    private static readonly Regex PromoStateRegex = new(
        @"data-promo-state=""([^""]+)""", RegexOptions.Compiled);

    // Matches the deadline inside <p class="deadline">...<strong>DATE</strong>...</p>
    private static readonly Regex DeadlineRegex = new(
        @"class=""deadline""[^>]*>.*?<strong>([^<]+)</strong>", RegexOptions.Compiled | RegexOptions.Singleline);

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
            var response = await _httpClient.GetAsync("/", cancellationToken);
            if (!response.IsSuccessStatusCode)
                return;

            var html = await response.Content.ReadAsStringAsync(cancellationToken);

            var stateMatch = PromoStateRegex.Match(html);
            bool promoActive = stateMatch.Success
                && stateMatch.Groups[1].Value.Equals("active", StringComparison.OrdinalIgnoreCase);

            var deadlineMatch = DeadlineRegex.Match(html);
            string deadline = deadlineMatch.Success ? deadlineMatch.Groups[1].Value.Trim() : string.Empty;

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
