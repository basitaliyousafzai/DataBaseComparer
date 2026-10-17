using SqlDrift.Core.Models;

namespace SqlDrift.Core.Extractor;

public interface ISchemaExtractor
{
    Task<DatabaseSchemaSnapshot> ExtractAsync(string target, CancellationToken cancellationToken = default);
}

public class UniversalSchemaExtractor : ISchemaExtractor
{
    private readonly LiveCatalogExtractorOptions _liveOptions;

    public UniversalSchemaExtractor(LiveCatalogExtractorOptions? liveOptions = null)
    {
        _liveOptions = liveOptions ?? new LiveCatalogExtractorOptions();
    }

    public async Task<DatabaseSchemaSnapshot> ExtractAsync(string target, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);

        // 1. Snapshot JSON file
        if (target.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            return await SnapshotSerializer.LoadSnapshotAsync(target, cancellationToken);
        }

        // 2. SQL file or directory
        if (target.EndsWith(".sql", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
        {
            var extractor = new ScriptDomExtractor(_liveOptions.DefaultSchema);
            var dbName = Path.GetFileNameWithoutExtension(target);
            return await extractor.ExtractFromDirectoryOrFileAsync(target, dbName, cancellationToken);
        }

        if (Directory.Exists(target))
        {
            var extractor = new ScriptDomExtractor(_liveOptions.DefaultSchema);
            var dbName = new DirectoryInfo(target).Name;
            return await extractor.ExtractFromDirectoryOrFileAsync(target, dbName, cancellationToken);
        }

        // 3. Assume Live Database Connection String
        var liveExtractor = new LiveCatalogExtractor(_liveOptions);
        return await liveExtractor.ExtractAsync(target, cancellationToken);
    }
}
