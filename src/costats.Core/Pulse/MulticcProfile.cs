namespace costats.Core.Pulse;

/// <summary>
/// Represents a discovered multicc profile with its configuration directory and auth type.
/// </summary>
public sealed record MulticcProfile(
    string Name,
    string ConfigDir,
    string AuthType,
    string? Description);
