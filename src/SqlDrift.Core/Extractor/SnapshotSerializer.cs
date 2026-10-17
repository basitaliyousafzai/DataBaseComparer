using System.Text.Json;
using System.Text.Json.Serialization;
using SqlDrift.Core.Models;

namespace SqlDrift.Core.Extractor;

public static class SnapshotSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(DatabaseSchemaSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, Options);
    }

    public static DatabaseSchemaSnapshot Deserialize(string json)
    {
        return JsonSerializer.Deserialize<DatabaseSchemaSnapshot>(json, Options)
            ?? throw new InvalidOperationException("Failed to deserialize schema snapshot JSON.");
    }

    public static async Task SaveSnapshotAsync(DatabaseSchemaSnapshot snapshot, string filePath, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var stream = File.Create(filePath);
        await JsonSerializer.SerializeAsync(stream, snapshot, Options, cancellationToken);
    }

    public static async Task<DatabaseSchemaSnapshot> LoadSnapshotAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Snapshot file not found: {filePath}", filePath);
        }

        await using var stream = File.OpenRead(filePath);
        var snapshot = await JsonSerializer.DeserializeAsync<DatabaseSchemaSnapshot>(stream, Options, cancellationToken);
        return snapshot ?? throw new InvalidOperationException($"Empty or invalid snapshot in {filePath}");
    }
}
