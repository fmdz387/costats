using System.Text.Json;
using System.Text.Json.Serialization;
using costats.Application.Settings;

namespace costats.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public JsonSettingsStore()
    {
        _serializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(path);
        try
        {
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return settings ?? new AppSettings();
        }
        catch (JsonException)
        {
            BackupCorruptSettings(path);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var path = GetSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, _serializerOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string GetSettingsPath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "costats", "settings.json");
    }

    private static void BackupCorruptSettings(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var backupPath = Path.Combine(directory, "settings.bad.json");
            File.Copy(path, backupPath, true);
        }
        catch
        {
            // Ignore backup failures.
        }
    }
}
