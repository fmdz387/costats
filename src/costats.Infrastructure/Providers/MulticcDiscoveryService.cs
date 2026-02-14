using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Infrastructure implementation of <see cref="IMulticcDiscovery"/>
/// that wraps <see cref="MulticcConfigReader"/>.
/// </summary>
public sealed class MulticcDiscoveryService : IMulticcDiscovery
{
    private readonly MulticcConfigReader _reader;
    private readonly string? _configPathOverride;
    private IReadOnlyList<MulticcProfile> _cachedProfiles;
    private bool _isDetected;

    public MulticcDiscoveryService(MulticcConfigReader reader, string? configPathOverride = null)
    {
        _reader = reader;
        _configPathOverride = configPathOverride;

        var configPath = configPathOverride is not null
            ? Path.Combine(configPathOverride, "config.json")
            : MulticcConfigReader.GetConfigPath();

        _isDetected = _reader.IsMulticcDetected(configPath);
        _cachedProfiles = _isDetected ? _reader.ReadProfiles(configPath) : [];
    }

    public bool IsDetected => _isDetected;
    public IReadOnlyList<MulticcProfile> Profiles => _cachedProfiles;

    public IReadOnlyList<MulticcProfile> Refresh()
    {
        var configPath = _configPathOverride is not null
            ? Path.Combine(_configPathOverride, "config.json")
            : MulticcConfigReader.GetConfigPath();

        _isDetected = _reader.IsMulticcDetected(configPath);
        _cachedProfiles = _isDetected ? _reader.ReadProfiles(configPath) : [];
        return _cachedProfiles;
    }
}
