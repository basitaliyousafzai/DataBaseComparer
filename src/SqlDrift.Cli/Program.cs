using Spectre.Console.Cli;
using SqlDrift.Cli.Commands;

namespace SqlDrift.Cli;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var app = new CommandApp();

        app.Configure(config =>
        {
            config.SetApplicationName("sqldrift");
            config.SetApplicationVersion("1.0.0");

            config.AddCommand<CompareCommand>("compare")
                .WithDescription("Compare two SQL Server databases, DDL scripts, or snapshots and print drift.")
                .WithExample("compare", "\"Server=localhost;Database=DevDb;Integrated Security=true\"", "\"Server=localhost;Database=ProdDb;Integrated Security=true\"")
                .WithExample("compare", "./source_schema.sql", "./target_schema.sql", "--format", "markdown");

            config.AddCommand<CompareBackupCommand>("compare-bak")
                .WithDescription("Compare two SQL Server .bak backup files by restoring them to a temporary database.")
                .WithExample("compare-bak", "./db_v1.bak", "./db_v2.bak", "--server", "\"Server=localhost;Integrated Security=true;\"");

            config.AddCommand<GenerateCommand>("generate")
                .WithDescription("Generate safe, idempotent migration and rollback T-SQL scripts.")
                .WithExample("generate", "./source.sql", "./target.sql", "-o", "migration.sql", "--allow-data-loss");

            config.AddCommand<GenerateCommand>("sync")
                .WithDescription("Alias for generate; creates safe migration script or applies to target.")
                .WithExample("sync", "./source_snapshot.json", "\"Server=localhost;Database=DevDb;...\"", "--apply");

            config.AddCommand<SnapshotCommand>("snapshot")
                .WithDescription("Extract schema metadata from a database or SQL scripts into a portable JSON snapshot.")
                .WithExample("snapshot", "\"Server=localhost;Database=DevDb;...\"", "-o", "dev_snapshot.json");

            config.AddCommand<CiCommand>("ci")
                .WithDescription("Run CI/CD automated drift inspection with pipeline exit codes and report generation.")
                .WithExample("ci", "./dev_snapshot.json", "./prod_snapshot.json", "--fail-on-breaking", "-f", "junit", "-o", "test-results.xml");

            config.AddCommand<UiCommand>("ui")
                .WithDescription("Launch the local interactive visual diff web interface.")
                .WithExample("ui", "./source.sql", "./target.sql", "--port", "5050");
        });

        return await app.RunAsync(args);
    }
}