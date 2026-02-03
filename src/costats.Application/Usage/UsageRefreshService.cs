using costats.Application.Abstractions;
using costats.Core.Models;

namespace costats.Application.Usage;

public sealed class UsageRefreshService
{
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly IClock _clock;

    public UsageRefreshService(IEnumerable<IUsageProvider> providers, IClock clock)
    {
        _providers = providers.ToList();
        _clock = clock;
    }

    public async Task<IReadOnlyList<ProviderSnapshot>> RefreshAsync(CancellationToken cancellationToken)
    {
        var results = new List<ProviderSnapshot>(_providers.Count);

        foreach (var provider in _providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = await provider.FetchAsync(cancellationToken);
            results.Add(snapshot with { CapturedAt = _clock.UtcNow });
        }

        return results;
    }
}
