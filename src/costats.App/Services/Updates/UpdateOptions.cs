using Microsoft.Extensions.Configuration;

namespace costats.App.Services.Updates;

public sealed class UpdateOptions
{
    public bool Enabled { get; init; } = true;
    public string Repository { get; init; } = "fmdz387/costats";
    public int CheckIntervalHours { get; init; } = 6;
    public bool AllowPrerelease { get; init; } = false;
    public bool ApplyStagedUpdateOnStartup { get; init; } = true;

    public static UpdateOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection("Costats:Update");

        return new UpdateOptions
        {
            Enabled = GetBool(section["Enabled"], defaultValue: true),
            Repository = string.IsNullOrWhiteSpace(section["Repository"]) ? "fmdz387/costats" : section["Repository"]!.Trim(),
            CheckIntervalHours = Math.Clamp(GetInt(section["CheckIntervalHours"], defaultValue: 6), 1, 168),
            AllowPrerelease = GetBool(section["AllowPrerelease"], defaultValue: false),
            ApplyStagedUpdateOnStartup = GetBool(section["ApplyStagedUpdateOnStartup"], defaultValue: true)
        };
    }

    private static bool GetBool(string? value, bool defaultValue)
    {
        return bool.TryParse(value, out var parsed) ? parsed : defaultValue;
    }

    private static int GetInt(string? value, int defaultValue)
    {
        return int.TryParse(value, out var parsed) ? parsed : defaultValue;
    }
}
