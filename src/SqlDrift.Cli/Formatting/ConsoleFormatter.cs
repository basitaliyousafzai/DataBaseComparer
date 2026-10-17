using Spectre.Console;
using SqlDrift.Core.Models;

namespace SqlDrift.Cli.Formatting;

public static class ConsoleFormatter
{
    public static void PrintReport(SchemaDriftReport report)
    {
        // 1. Header Banner
        var rule = new Rule("[bold cyan]SQLDrift Schema Comparison[/]")
        {
            Justification = Justify.Left
        };
        AnsiConsole.Write(rule);

        var grid = new Grid();
        grid.AddColumn();
        grid.AddColumn();
        grid.AddRow("[bold grey]Source:[/] ", $"[cyan]{Markup.Escape(report.SourceIdentifier)}[/]");
        grid.AddRow("[bold grey]Target:[/] ", $"[magenta]{Markup.Escape(report.TargetIdentifier)}[/]");
        grid.AddRow("[bold grey]Analyzed At:[/] ", $"{report.AnalyzedAtUtc:yyyy-MM-dd HH:mm:ss} UTC");
        AnsiConsole.Write(grid);
        AnsiConsole.WriteLine();

        // 2. Metrics Table
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey37)
            .AddColumn(new TableColumn("[bold]Metric[/]"))
            .AddColumn(new TableColumn("[bold]Count[/]").Centered())
            .AddColumn(new TableColumn("[bold]Impact[/]"));

        table.AddRow("Total Objects Drifted", $"[bold]{report.TotalDrifts}[/]", report.HasDrift ? "[yellow]Differences Detected[/]" : "[green]In Sync[/]");
        table.AddRow("🚨 Breaking / Data Loss", $"[bold red]{report.BreakingCount}[/]", report.BreakingCount > 0 ? "[red]Requires --allow-data-loss[/]" : "[green]None[/]");
        table.AddRow("⚠️ Medium Severity", $"[bold yellow]{report.MediumCount}[/]", report.MediumCount > 0 ? "[yellow]Modified definitions[/]" : "[green]None[/]");
        table.AddRow("ℹ️ Safe Additions (Low)", $"[bold green]{report.LowCount}[/]", "[grey]Additive changes[/]");
        table.AddRow("➕ Added in Target", $"{report.AddedCount}", "[grey]Objects to be created[/]");
        table.AddRow("➖ Missing from Source", $"{report.MissingCount}", "[grey]Objects to be dropped[/]");
        table.AddRow("✏️ Modified in Target", $"{report.ModifiedCount}", "[grey]Objects altered[/]");

        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();

        if (report.Drifts.Count == 0)
        {
            AnsiConsole.MarkupLine("[bold green]✔ Target schema is 100% in sync with source. No drift detected![/]\n");
            return;
        }

        // 3. Color-coded Drift Tree
        var tree = new Tree("[bold white]Schema Drift Tree[/]").Guide(TreeGuide.Line);

        var tableNodes = tree.AddNode("[bold cyan]Tables[/]");
        var procNodes = tree.AddNode("[bold yellow]Stored Procedures[/]");
        var viewNodes = tree.AddNode("[bold blue]Views[/]");
        var funcNodes = tree.AddNode("[bold magenta]Functions[/]");
        var trigNodes = tree.AddNode("[bold orange3]Triggers[/]");

        foreach (var drift in report.Drifts)
        {
            var targetParent = drift.ObjectType switch
            {
                DatabaseObjectType.Table => tableNodes,
                DatabaseObjectType.StoredProcedure => procNodes,
                DatabaseObjectType.View => viewNodes,
                DatabaseObjectType.UserDefinedFunction => funcNodes,
                DatabaseObjectType.Trigger => trigNodes,
                _ => tableNodes
            };

            var sevBadge = drift.Severity switch
            {
                DriftSeverity.Breaking => "[red bold]BREAKING[/]",
                DriftSeverity.Medium => "[yellow]MEDIUM[/]",
                DriftSeverity.Low => "[green]LOW[/]",
                _ => "[grey]NONE[/]"
            };

            var driftTypeBadge = drift.DriftType switch
            {
                DriftType.Added => "[green]+ ADDED[/]",
                DriftType.Missing => "[red]- MISSING[/]",
                DriftType.Modified => "[yellow]~ MODIFIED[/]",
                _ => "[grey]UNCHANGED[/]"
            };

            var nodeText = $"{driftTypeBadge} [white bold]{Markup.Escape(drift.FullName)}[/] ({sevBadge}) - [grey]{Markup.Escape(drift.Description)}[/]";
            var objNode = targetParent.AddNode(nodeText);

            if (drift.Children != null && drift.Children.Count > 0)
            {
                foreach (var child in drift.Children)
                {
                    var childSevBadge = child.Severity switch
                    {
                        DriftSeverity.Breaking => "[red]BREAKING[/]",
                        DriftSeverity.Medium => "[yellow]MEDIUM[/]",
                        DriftSeverity.Low => "[green]LOW[/]",
                        _ => "[grey]NONE[/]"
                    };

                    var childTypeBadge = child.DriftType switch
                    {
                        DriftType.Added => "[green]+[/]",
                        DriftType.Missing => "[red]-[/]",
                        DriftType.Modified => "[yellow]~[/]",
                        _ => "[grey]•[/]"
                    };

                    objNode.AddNode($"{childTypeBadge} [white]{Markup.Escape(child.Name)}[/] [grey][[{child.ObjectType}]][/] ({childSevBadge}) - [grey]{Markup.Escape(child.Description)}[/]");
                }
            }
        }

        AnsiConsole.Write(tree);
        AnsiConsole.WriteLine();
    }
}
