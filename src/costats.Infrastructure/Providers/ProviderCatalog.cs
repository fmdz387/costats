using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public static class ProviderCatalog
{
    public static ProviderProfile Codex { get; } = new("codex", "Codex", "#0A84FF");
    public static ProviderProfile Claude { get; } = new("claude", "Claude", "#FF7A00");
}
