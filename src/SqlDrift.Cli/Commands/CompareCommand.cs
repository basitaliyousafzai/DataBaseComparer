using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlDrift.Cli.Formatting;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Reporting;

namespace SqlDrift.Cli.Commands;

public class CompareCommand : AsyncCommand<CompareCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Source database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(0, "<source>")]
        public string Source { get; set; } = string.Empty;

        [Description("Target database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(1, "<target>")]
        public string Target { get; set; } = string.Empty;

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

        [Description("Enforce strict collation comparison.")]
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

        var (sourceSnapshot, targetSnapshot) = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Extracting schema metadata...", async ctx =>
            {
                ctx.Status($"Extracting source: [cyan]{settings.Source}[/]");
                var src = await extractor.ExtractAsync(settings.Source);

                ctx.Status($"Extracting target: [magenta]{settings.Target}[/]");
                var tgt = await extractor.ExtractAsync(settings.Target);

                return (src, tgt);
            });

        var differ = new SchemaDiffer();
        var report = differ.Compare(sourceSnapshot, targetSnapshot, settings.Source, settings.Target);

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
        {
            AnsiConsole.MarkupLine("[bold red]CI check failed: Breaking schema drift detected![/]");
            return 1;
        }

        if (settings.FailOnDrift && report.HasDrift)
        {
            AnsiConsole.MarkupLine("[bold red]CI check failed: Schema drift detected![/]");
            return 1;
        }

        return 0;
    }
}
