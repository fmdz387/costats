using costats.Core.Pulse;

namespace costats.Application.Pulse;

public interface ISignalSource
{
    ProviderProfile Profile { get; }

    Task<ProviderReading> ReadAsync(CancellationToken cancellationToken);
}
