using System.ComponentModel;
using Microsoft.Data.SqlClient;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlDrift.Cli.Formatting;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Extractor;
using SqlDrift.Core.Generator;
using SqlDrift.Core.Planner;

namespace SqlDrift.Cli.Commands;

public class GenerateCommand : AsyncCommand<GenerateCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Source database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(0, "<source>")]
        public string Source { get; set; } = string.Empty;

        [Description("Target database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(1, "<target>")]
        public string Target { get; set; } = string.Empty;

        [Description("Output file path for the migration SQL script.")]
        [CommandOption("-o|--out")]
        public string? OutFile { get; set; }

        [Description("Output file path for the reversal/rollback SQL script.")]
        [CommandOption("--rollback-out")]
        public string? RollbackOutFile { get; set; }

        [Description("Permit destructive operations (DROP COLUMN, DROP TABLE, data-loss type narrowing).")]
        [CommandOption("--allow-data-loss")]
        [DefaultValue(false)]
        public bool AllowDataLoss { get; set; }

        [Description("Do not wrap migration script in a transaction block.")]
        [CommandOption("--no-transaction")]
        [DefaultValue(false)]
        public bool NoTransaction { get; set; }

        [Description("Apply migration directly to the target database (requires live target connection string).")]
        [CommandOption("--apply")]
        [DefaultValue(false)]
        public bool Apply { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var extractor = new UniversalSchemaExtractor();

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

        ConsoleFormatter.PrintReport(report);

        if (!report.HasDrift)
        {
            AnsiConsole.MarkupLine("[green]✔ Database schemas are already identical. No migration required.[/]");
            return 0;
        }

        var planner = new MigrationPlanner();
        var plan = planner.BuildPlan(report, sourceSnapshot, targetSnapshot);

        if (plan.ContainsBreakingChanges && !settings.AllowDataLoss)
        {
            AnsiConsole.MarkupLine("[bold yellow]⚠️ Notice: Breaking changes detected (drops/type narrowing).[/]");
            AnsiConsole.MarkupLine("[yellow]Destructive operations are guarded in commented blocks. Pass [bold]--allow-data-loss[/] to execute active drops.[/]\n");
        }

        var generator = new MigrationScriptGenerator(new GeneratorOptions
        {
            AllowDataLoss = settings.AllowDataLoss,
            WrapInTransaction = !settings.NoTransaction,
            TransactionName = "SqlDrift_Sync"
        });

        var migrationSql = generator.GenerateMigrationScript(plan, settings.Source, settings.Target);

        if (!string.IsNullOrWhiteSpace(settings.OutFile))
        {
            await File.WriteAllTextAsync(settings.OutFile, migrationSql);
            AnsiConsole.MarkupLine($"[bold green]✔ Migration script written to:[/] [cyan]{settings.OutFile}[/]");
        }
        else if (!settings.Apply)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[bold cyan]Generated Migration Script[/]"));
            Console.WriteLine(migrationSql);
        }

        if (!string.IsNullOrWhiteSpace(settings.RollbackOutFile))
        {
            var rollbackSql = generator.GenerateRollbackScript(report, sourceSnapshot, targetSnapshot);
            await File.WriteAllTextAsync(settings.RollbackOutFile, rollbackSql);
            AnsiConsole.MarkupLine($"[bold green]✔ Rollback script written to:[/] [cyan]{settings.RollbackOutFile}[/]");
        }

        if (settings.Apply)
        {
            if (plan.ContainsBreakingChanges && !settings.AllowDataLoss)
            {
                AnsiConsole.MarkupLine("[bold red]Cannot apply migration directly: Breaking changes present and --allow-data-loss was not passed.[/]");
                return 1;
            }

            var confirm = AnsiConsole.Confirm($"[bold red]Are you sure you want to execute this migration against target '{settings.Target}'?[/]", false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[grey]Migration execution aborted by user.[/]");
                return 0;
            }

            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Executing migration against target...", async ctx =>
                {
                    await using var conn = new SqlConnection(settings.Target);
                    await conn.OpenAsync();
                    await using var cmd = new SqlCommand(migrationSql, conn);
                    cmd.CommandTimeout = 300;
                    await cmd.ExecuteNonQueryAsync();
                });

            AnsiConsole.MarkupLine("[bold green]✔ Migration successfully applied to target database![/]");
        }

        return 0;
    }
}
