using costats.Application.Pricing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace costats.Infrastructure.Pricing;

public sealed class PricingRefreshService : BackgroundService
{
    private readonly PricingCatalog _pricingCatalog;
    private readonly IOptions<PricingOptions> _options;
    private readonly ILogger<PricingRefreshService> _logger;

    public PricingRefreshService(
        PricingCatalog pricingCatalog,
        IOptions<PricingOptions> options,
        ILogger<PricingRefreshService> logger)
    {
        _pricingCatalog = pricingCatalog;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshSafelyAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(_options.Value.RefreshInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await RefreshSafelyAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pricingCatalog.RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Background pricing refresh failed");
        }
    }
}
