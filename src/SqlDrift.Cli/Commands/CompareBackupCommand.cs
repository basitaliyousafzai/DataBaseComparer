using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlDrift.Cli.Formatting;
using SqlDrift.Core.Backup;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Models;
using SqlDrift.Core.Reporting;

namespace SqlDrift.Cli.Commands;

public class CompareBackupCommand : AsyncCommand<CompareBackupCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Path to the source .bak backup file.")]
        [CommandArgument(0, "<backup1>")]
        public string Backup1 { get; set; } = string.Empty;

        [Description("Path to the target .bak backup file.")]
        [CommandArgument(1, "<backup2>")]
        public string Backup2 { get; set; } = string.Empty;

        [Description("SQL Server instance connection string used to restore backups temporarily.")]
        [CommandOption("--server")]
        [DefaultValue("Server=localhost;Integrated Security=true;TrustServerCertificate=true;")]
        public string Server { get; set; } = "Server=localhost;Integrated Security=true;TrustServerCertificate=true;";

        [Description("Output format: console, json, junit, markdown. Default is console.")]
        [CommandOption("-f|--format")]
        [DefaultValue("console")]
        public string Format { get; set; } = "console";

        [Description("Write output to file path instead of stdout.")]
        [CommandOption("-o|--out")]
        public string? OutFile { get; set; }

        [Description("Exit with non-zero code (1) if any schema drift is detected.")]
        [CommandOption("--fail-on-drift")]
        [DefaultValue(false)]
        public bool FailOnDrift { get; set; }

        [Description("Exit with non-zero code (1) if breaking changes or data loss are detected.")]
        [CommandOption("--fail-on-breaking")]
        [DefaultValue(false)]
        public bool FailOnBreaking { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        RestoredDatabase? restored1 = null;
        RestoredDatabase? restored2 = null;
        try
        {
            restored1 = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Restoring backup: {settings.Backup1}", async _ =>
                    await BackupRestoreService.RestoreAsync(settings.Server, settings.Backup1));

            restored2 = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync($"Restoring backup: {settings.Backup2}", async _ =>
                    await BackupRestoreService.RestoreAsync(settings.Server, settings.Backup2));

            var extractor = new UniversalSchemaExtractor();
            var sourceSnapshot = await extractor.ExtractAsync(restored1.ConnectionString);
            var targetSnapshot = await extractor.ExtractAsync(restored2.ConnectionString);

            var differ = new SchemaDiffer();
            var report = differ.Compare(sourceSnapshot, targetSnapshot, settings.Backup1, settings.Backup2);

            var output = settings.Format.ToLowerInvariant() switch
            {
                "json" => ReportFormatters.ToJson(report),
                "junit" => ReportFormatters.ToJUnitXml(report),
                "markdown" => ReportFormatters.ToMarkdown(report),
                _ => null
            };

            if (output != null)
            {
                if (!string.IsNullOrWhiteSpace(settings.OutFile))
                {
                    await File.WriteAllTextAsync(settings.OutFile, output);
                    AnsiConsole.MarkupLine($"[green]✔ Report saved to:[/] {settings.OutFile}");
                }
                else
                {
                    Console.WriteLine(output);
                }
            }
            else
            {
                ConsoleFormatter.PrintReport(report);
                if (!string.IsNullOrWhiteSpace(settings.OutFile))
                {
                    var md = ReportFormatters.ToMarkdown(report);
                    await File.WriteAllTextAsync(settings.OutFile, md);
                    AnsiConsole.MarkupLine($"[green]✔ Markdown report saved to:[/] {settings.OutFile}");
                }
            }

            if (settings.FailOnBreaking && report.HasBreakingDrift)
                return 1;
            if (settings.FailOnDrift && report.HasDrift)
                return 1;
            return 0;
        }
        finally
        {
            restored2?.Dispose();
            restored1?.Dispose();
        }
    }
}