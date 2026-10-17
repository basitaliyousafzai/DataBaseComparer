using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Extractor;
using SqlDrift.Server;

namespace SqlDrift.Cli.Commands;

public class UiCommand : AsyncCommand<UiCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [Description("Source database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(0, "<source>")]
        public string Source { get; set; } = string.Empty;

        [Description("Target database connection string, .sql script, or .json snapshot.")]
        [CommandArgument(1, "<target>")]
        public string Target { get; set; } = string.Empty;

        [Description("Port number for the embedded local web server. Default is 5050.")]
        [CommandOption("-p|--port")]
        [DefaultValue(5050)]
        public int Port { get; set; } = 5050;

        [Description("Do not automatically open the browser.")]
        [CommandOption("--no-browser")]
        [DefaultValue(false)]
        public bool NoBrowser { get; set; }
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

        AnsiConsole.MarkupLine($"[green]✔ Schema comparison complete:[/] {report.TotalDrifts} object(s) drifted.");
        AnsiConsole.MarkupLine($"Launching embedded web UI on port [cyan]{settings.Port}[/]...");

        await SqlDriftServer.StartAsync(
            report,
            sourceSnapshot,
            targetSnapshot,
            settings.Port,
            openBrowser: !settings.NoBrowser
        );

        return 0;
    }
}
