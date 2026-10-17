using Microsoft.Data.SqlClient;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Models;

namespace SqlDrift.Core.Backup;

public class RestoredDatabase : IAsyncDisposable, IDisposable
{
    public string DatabaseName { get; }
    public string ConnectionString { get; }
    private readonly string _serverConnectionString;
    private bool _disposed;

    public RestoredDatabase(string databaseName, string connectionString, string serverConnectionString)
    {
        DatabaseName = databaseName;
        ConnectionString = connectionString;
        _serverConnectionString = serverConnectionString;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            await DropDatabaseAsync(_serverConnectionString, DatabaseName);
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public static async Task DropDatabaseAsync(string serverConnectionString, string databaseName, CancellationToken cancellationToken = default)
    {
        var builder = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master" };
        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(cancellationToken);

        var sql = $"""
            IF DB_ID(N'{databaseName.Replace("'", "''")}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """;

        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 120 };
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}

public static class BackupRestoreService
{
    public static async Task<RestoredDatabase> RestoreAsync(
        string serverConnectionString,
        string backupFilePath,
        string? desiredDatabaseName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverConnectionString);
        if (!File.Exists(backupFilePath))
            throw new FileNotFoundException("Backup file not found.", backupFilePath);

        var serverBuilder = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master", ConnectTimeout = 5 };

        // Determine default data/log paths
        string dataPath;
        string logPath;
        await using (var conn = new SqlConnection(serverBuilder.ConnectionString))
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand("SELECT SERVERPROPERTY('InstanceDefaultDataPath'), SERVERPROPERTY('InstanceDefaultLogPath')", conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Unable to query server default paths.");
            dataPath = reader.IsDBNull(0) ? @"C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER\MSSQL\DATA\" : reader.GetString(0);
            logPath = reader.IsDBNull(1) ? dataPath : reader.GetString(1);
        }

        // Get logical file names
        var logicalFiles = new List<(string LogicalName, string Type)>();
        await using (var conn = new SqlConnection(serverBuilder.ConnectionString))
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand("RESTORE FILELISTONLY FROM DISK = @disk", conn) { CommandTimeout = 300 };
            cmd.Parameters.AddWithValue("@disk", backupFilePath);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var logicalName = reader["LogicalName"]?.ToString() ?? string.Empty;
                var type = reader["Type"]?.ToString() ?? "D";
                if (!string.IsNullOrWhiteSpace(logicalName))
                    logicalFiles.Add((logicalName, type));
            }
        }

        if (logicalFiles.Count == 0)
            throw new InvalidOperationException("No logical files found in backup.");

        // Determine target database name
        var dbName = string.IsNullOrWhiteSpace(desiredDatabaseName)
            ? $"SqlDrift_Restore_{Guid.NewGuid():N}"
            : SanitizeDatabaseName(desiredDatabaseName);

        // Build MOVE clauses
        var moveClauses = new List<string>();
        int dataIdx = 0;
        int logIdx = 0;
        for (int i = 0; i < logicalFiles.Count; i++)
        {
            var (logical, type) = logicalFiles[i];
            string targetFile;
            if (type.Equals("L", StringComparison.OrdinalIgnoreCase))
            {
                targetFile = Path.Combine(logPath, $"{dbName}_{logIdx}.ldf");
                logIdx++;
            }
            else
            {
                targetFile = Path.Combine(dataPath, dataIdx == 0 ? $"{dbName}.mdf" : $"{dbName}_{dataIdx}.ndf");
                dataIdx++;
            }
            moveClauses.Add($"MOVE N'{logical.Replace("'", "''")}' TO N'{targetFile.Replace("'", "''")}'");
        }

        var restoreSql = $"RESTORE DATABASE [{dbName}] FROM DISK = @disk WITH {string.Join(", ", moveClauses)}, REPLACE, RECOVERY, STATS = 10;";

        await using (var conn = new SqlConnection(serverBuilder.ConnectionString))
        {
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new SqlCommand(restoreSql, conn) { CommandTimeout = 600 };
            cmd.Parameters.AddWithValue("@disk", backupFilePath);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        // Build connection string to restored DB
        var restoredBuilder = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = dbName };
        return new RestoredDatabase(dbName, restoredBuilder.ConnectionString, serverBuilder.ConnectionString);
    }

    private static string SanitizeDatabaseName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        if (string.IsNullOrWhiteSpace(sanitized))
            sanitized = $"SqlDrift_Restore_{Guid.NewGuid():N}";
        return sanitized;
    }
}

public static class BackupSchemaExtractor
{
    public static async Task<DatabaseSchemaSnapshot> ExtractFromBackupAsync(
        string serverConnectionString,
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        using var restored = await BackupRestoreService.RestoreAsync(serverConnectionString, backupFilePath, cancellationToken: cancellationToken);
        var extractor = new UniversalSchemaExtractor();
        return await extractor.ExtractAsync(restored.ConnectionString, cancellationToken);
    }
}