using costats.Core.Pulse;

namespace costats.Application.Pulse;

public interface ISourceSelector
{
    Task<ProviderReading> SelectAsync(
        string providerId,
        IReadOnlyList<ISignalSource> sources,
        CancellationToken cancellationToken);
}
