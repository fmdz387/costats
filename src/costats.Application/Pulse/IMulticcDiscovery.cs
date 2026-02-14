using costats.Core.Pulse;

namespace costats.Application.Pulse;

/// <summary>
/// Application-layer abstraction for multicc profile discovery.
/// </summary>
public interface IMulticcDiscovery
{
    /// <summary>
    /// Returns true if multicc is installed and detected on this machine.
    /// </summary>
    bool IsDetected { get; }

    /// <summary>
    /// Returns all discovered multicc profiles. Empty if not detected.
    /// </summary>
    IReadOnlyList<MulticcProfile> Profiles { get; }

    /// <summary>
    /// Re-reads the multicc config from disk and returns updated profiles.
    /// </summary>
    IReadOnlyList<MulticcProfile> Refresh();
}
