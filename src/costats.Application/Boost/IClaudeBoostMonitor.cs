namespace costats.Application.Boost;

/// <summary>
/// Represents the current Claude 2x limits promotional status from isclaude2x.com.
/// During the Anthropic promo period (March 13–27 2026), usage limits are doubled
/// outside peak hours (8 AM – 2 PM ET on weekdays) and all day on weekends.
/// </summary>
public record ClaudeBoostState(
    bool Is2x,
    bool PromoActive,
    bool IsPeak,
    bool IsWeekend,
    string ExpiresIn,
    DateTimeOffset FetchedAt);

/// <summary>
/// Monitors whether the Claude 2x promotional limits are currently active.
/// </summary>
public interface IClaudeBoostMonitor
{
    ClaudeBoostState? Current { get; }
    event EventHandler? StatusChanged;
}
