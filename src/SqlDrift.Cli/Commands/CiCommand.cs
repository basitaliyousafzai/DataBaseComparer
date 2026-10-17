using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlDrift.Cli.Formatting;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Reporting;

namespace SqlDrift.Cli.Commands;

public class CiCommand : AsyncCommand<CiCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Source database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(0, "<source>")]
        public string Source { get; set; } = string.Empty;

        [Description("Target database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(1, "<target>")]
        public string Target { get; set; } = string.Empty;

        [Description("Fail CI build (exit code 1) if any schema drift exists.")]
        [CommandOption("--fail-on-drift")]
        [DefaultValue(true)]
        public bool FailOnDrift { get; set; }

        [Description("Fail CI build (exit code 1) only if breaking / destructive schema drift exists.")]
        [CommandOption("--fail-on-breaking")]
        [DefaultValue(false)]
        public bool FailOnBreaking { get; set; }

        [Description("Report format: markdown, junit, json, console. Default is markdown.")]
        [CommandOption("-f|--format")]
        [DefaultValue("markdown")]
        public string Format { get; set; } = "markdown";

        [Description("Path to write report file (e.g. for GitHub Actions step summary or test results).")]
        [CommandOption("-o|--out")]
        public string? OutFile { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var extractor = new UniversalSchemaExtractor();
        var sourceSnapshot = await extractor.ExtractAsync(settings.Source);
        var targetSnapshot = await extractor.ExtractAsync(settings.Target);

        var differ = new SchemaDiffer();
        var report = differ.Compare(sourceSnapshot, targetSnapshot, settings.Source, settings.Target);

        var reportContent = settings.Format.ToLowerInvariant() switch
        {
            "junit" => ReportFormatters.ToJUnitXml(report),
            "json" => ReportFormatters.ToJson(report),
            "console" => null,
            _ => ReportFormatters.ToMarkdown(report)
        };

        if (reportContent != null)
        {
            if (!string.IsNullOrWhiteSpace(settings.OutFile))
            {
                await File.WriteAllTextAsync(settings.OutFile, reportContent);
            }
            else
            {
                Console.WriteLine(reportContent);
            }
        }
        else
        {
            ConsoleFormatter.PrintReport(report);
        }

        // Check if running inside GitHub Actions and outputting markdown
        var githubStepSummary = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (!string.IsNullOrWhiteSpace(githubStepSummary))
        {
            var md = ReportFormatters.ToMarkdown(report);
            await File.AppendAllTextAsync(githubStepSummary, md);
        }

        if (settings.FailOnBreaking && report.HasBreakingDrift)
        {
            AnsiConsole.MarkupLine($"[bold red]CI Pipeline Failed:[/] {report.BreakingCount} breaking schema changes detected.");
            return 1;
        }

        if (settings.FailOnDrift && report.HasDrift)
        {
            AnsiConsole.MarkupLine($"[bold red]CI Pipeline Failed:[/] {report.TotalDrifts} schema drift differences detected.");
            return 1;
        }

        AnsiConsole.MarkupLine("[bold green]✔ CI Pipeline Passed: Schema check succeeded without drift violations.[/]");
        return 0;
    }
}
