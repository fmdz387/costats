using System.Text.Json;
using costats.Core.Pulse;
using Microsoft.Extensions.Logging;

namespace costats.Infrastructure.Providers;

/// <summary>
/// Reads and validates the multicc configuration file (~/.multicc/config.json)
/// to discover available Claude Code profiles.
/// </summary>
public sealed class MulticcConfigReader
{
    private readonly ILogger<MulticcConfigReader> _logger;

    public MulticcConfigReader(ILogger<MulticcConfigReader> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns the default multicc directory path.
    /// Checks MULTICC_DIR env var first, falls back to ~/.multicc.
    /// </summary>
    public static string GetMulticcDir()
    {
        var envDir = Environment.GetEnvironmentVariable("MULTICC_DIR");
        if (!string.IsNullOrWhiteSpace(envDir))
            return envDir.Trim();

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".multicc");
    }

    /// <summary>
    /// Returns the multicc config file path.
    /// </summary>
    public static string GetConfigPath(string? multiccDir = null)
    {
        return Path.Combine(multiccDir ?? GetMulticcDir(), "config.json");
    }

    /// <summary>
    /// Checks whether multicc is installed (config file exists).
    /// </summary>
    public bool IsMulticcDetected(string? configPath = null)
    {
        var path = configPath ?? GetConfigPath();
        return File.Exists(path);
    }

    /// <summary>
    /// Reads the multicc config and returns all discovered profiles.
    /// Returns empty list on any error (graceful degradation).
    /// </summary>
    public IReadOnlyList<MulticcProfile> ReadProfiles(string? configPath = null)
    {
        var path = configPath ?? GetConfigPath();
        if (!File.Exists(path))
        {
            _logger.LogDebug("Multicc config not found at {Path}", path);
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Validate version field matches multicc config schema v1
            if (!root.TryGetProperty("version", out var versionEl) ||
                versionEl.GetInt32() != 1)
            {
                _logger.LogWarning("Multicc config has unsupported version at {Path}", path);
                return [];
            }

            if (!root.TryGetProperty("profiles", out var profilesEl) ||
                profilesEl.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var profiles = new List<MulticcProfile>();
            foreach (var prop in profilesEl.EnumerateObject())
            {
                var name = prop.Name;
                var profile = prop.Value;

                var authType = profile.TryGetProperty("authType", out var at)
                    ? at.GetString() ?? "oauth"
                    : "oauth";
                var configDir = profile.TryGetProperty("configDir", out var cd)
                    ? cd.GetString()
                    : null;
                var description = profile.TryGetProperty("description", out var desc)
                    ? desc.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(configDir))
                {
                    _logger.LogWarning("Multicc profile '{Name}' has no configDir, skipping", name);
                    continue;
                }

                if (!Directory.Exists(configDir))
                {
                    _logger.LogWarning("Multicc profile '{Name}' configDir does not exist: {Dir}", name, configDir);
                    continue;
                }

                profiles.Add(new MulticcProfile(name, configDir, authType, description));
            }

            _logger.LogInformation("Discovered {Count} multicc profiles", profiles.Count);
            return profiles;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read multicc config at {Path}", path);
            return [];
        }
    }
}
