using costats.Core.Models;
using costats.Core.Providers;

namespace costats.Application.Abstractions;

public interface IUsageProvider
{
    ProviderDescriptor Descriptor { get; }

    Task<ProviderSnapshot> FetchAsync(CancellationToken cancellationToken);
}
