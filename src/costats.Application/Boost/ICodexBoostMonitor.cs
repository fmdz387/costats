namespace costats.Application.Boost;

/// <summary>
/// Represents the current Codex 2x limits promotional status from iscodex2x.com.
/// Unlike Claude's 2x promo, Codex 2x runs around the clock (no peak-hour restrictions)
/// for paid plans through the promo deadline.
/// </summary>
public record CodexBoostState(
    bool PromoActive,
    string Deadline,
    DateTimeOffset FetchedAt);

/// <summary>
/// Monitors whether the Codex 2x promotional limits are currently active.
/// </summary>
public interface ICodexBoostMonitor
{
    CodexBoostState? Current { get; }
    event EventHandler? StatusChanged;
}
