using costats.Core.Pulse;

namespace costats.Application.Pulse;

public interface IPulseSnapshotWriter
{
    Task WriteAsync(PulseState state, CancellationToken cancellationToken);
}
