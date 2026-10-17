using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlDrift.Core.Extractor;

namespace SqlDrift.Cli.Commands;

public class SnapshotCommand : AsyncCommand<SnapshotCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Source database connection string or directory of .sql scripts.")]
        [CommandArgument(0, "<source>")]
        public string Source { get; set; } = string.Empty;

        [Description("Output JSON file path. Default: <dbname>_snapshot.json.")]
        [CommandOption("-o|--out")]
        public string? OutFile { get; set; }

        [Description("Enforce strict collation extraction.")]
        [CommandOption("--strict-collation")]
        [DefaultValue(false)]
        public bool StrictCollation { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var extractor = new UniversalSchemaExtractor(new LiveCatalogExtractorOptions
        {
            StrictCollation = settings.StrictCollation
        });

        var snapshot = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Extracting schema snapshot for '{settings.Source}'...", async _ =>
            {
                return await extractor.ExtractAsync(settings.Source);
            });

        var targetFile = settings.OutFile;
        if (string.IsNullOrWhiteSpace(targetFile))
        {
            var sanitizedDbName = string.Join("_", snapshot.DatabaseName.Split(Path.GetInvalidFileNameChars()));
            targetFile = $"{sanitizedDbName}_snapshot.json";
        }

        await SnapshotSerializer.SaveSnapshotAsync(snapshot, targetFile);

        var fileInfo = new FileInfo(targetFile);

        AnsiConsole.MarkupLine($"[bold green]✔ Schema snapshot successfully created![/]");
        AnsiConsole.MarkupLine($"  [grey]File:[/]       [cyan]{targetFile}[/]");
        AnsiConsole.MarkupLine($"  [grey]Size:[/]       {fileInfo.Length:N0} bytes");
        AnsiConsole.MarkupLine($"  [grey]Tables:[/]     {snapshot.Tables.Count}");
        AnsiConsole.MarkupLine($"  [grey]Objects:[/]    {snapshot.ProgrammableObjects.Count}");

        return 0;
    }
}
