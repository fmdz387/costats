namespace costats.Application.Settings;

public sealed class AppSettings
{
    public int RefreshMinutes { get; set; } = 5;
    public string Hotkey { get; set; } = "Ctrl+Alt+U";
    public bool StartAtLogin { get; set; } = false;
}
