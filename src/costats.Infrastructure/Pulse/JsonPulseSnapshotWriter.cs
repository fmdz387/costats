using System.Text.Json;
using costats.Application.Pulse;
using costats.Core.Pulse;

namespace costats.Infrastructure.Pulse;

public sealed class JsonPulseSnapshotWriter : IPulseSnapshotWriter
{
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public async Task WriteAsync(PulseState state, CancellationToken cancellationToken)
    {
        var path = GetSnapshotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, state, _serializerOptions, cancellationToken);
    }

    private static string GetSnapshotPath()
    {
        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "costats", "snapshots", "pulse.json");
    }
}
