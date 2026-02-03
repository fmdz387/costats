namespace costats.Application.Pulse;

public sealed class PulseOptions
{
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
}
