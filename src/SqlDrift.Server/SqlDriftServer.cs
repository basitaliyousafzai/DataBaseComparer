using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Generator;
using SqlDrift.Core.Models;
using SqlDrift.Core.Planner;

namespace SqlDrift.Server;

public record GenerateScriptRequest(
    List<int>? SelectedIndices,
    bool AllowDataLoss = false,
    bool WrapInTransaction = true
);

public class SqlDriftServer
{
    public static async Task StartAsync(
        SchemaDriftReport report,
        DatabaseSchemaSnapshot source,
        DatabaseSchemaSnapshot target,
        int port = 5050,
        bool openBrowser = true,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateBuilder();

        // Suppress verbose logging to keep terminal clean
        builder.Logging.ClearProviders();
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Logging.AddFilter("System", LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.ListenLocalhost(port);
        });

        builder.Services.AddRouting();

        var app = builder.Build();

        var embeddedHtml = LoadEmbeddedHtml();

        // 1. Serve SPA HTML
        app.MapGet("/", () => Results.Content(embeddedHtml, "text/html; charset=utf-8"));

        // 2. Report JSON
        app.MapGet("/api/report", () => Results.Json(report));

        // 3. Health
        app.MapGet("/api/health", () => Results.Ok(new
        {
            Status = "Healthy",
            Source = report.SourceIdentifier,
            Target = report.TargetIdentifier,
            TotalDrifts = report.TotalDrifts,
            HasBreakingDrift = report.HasBreakingDrift
        }));

        // 4. Detailed Diff
        app.MapGet("/api/diff/{objectType}/{schema}/{name}", (string objectType, string schema, string name) =>
        {
            var drift = report.Drifts.FirstOrDefault(d =>
                d.ObjectType.ToString().Equals(objectType, StringComparison.OrdinalIgnoreCase) &&
                d.Schema.Equals(schema, StringComparison.OrdinalIgnoreCase) &&
                d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            if (drift == null)
            {
                return Results.NotFound(new { Message = $"Object {objectType} [{schema}].[{name}] not found in report." });
            }

            var diff = TextDiffService.BuildSideBySideDiff(drift.TargetDefinition ?? "", drift.SourceDefinition ?? "");
            return Results.Ok(new { Drift = drift, Diff = diff });
        });

        // 5. Generate Script
        app.MapPost("/api/generate", (GenerateScriptRequest req) =>
        {
            var driftsToInclude = report.Drifts;
            if (req.SelectedIndices != null && req.SelectedIndices.Count > 0)
            {
                var indexSet = req.SelectedIndices.ToHashSet();
                driftsToInclude = report.Drifts.Where((_, idx) => indexSet.Contains(idx)).ToList();
            }

            var filteredReport = new SchemaDriftReport(
                report.SourceIdentifier,
                report.TargetIdentifier,
                DateTime.UtcNow,
                driftsToInclude
            );

            var planner = new MigrationPlanner();
            var plan = planner.BuildPlan(filteredReport, source, target);

            var generator = new MigrationScriptGenerator(new GeneratorOptions
            {
                AllowDataLoss = req.AllowDataLoss,
                WrapInTransaction = req.WrapInTransaction,
                TransactionName = "SqlDrift_UI_Migration"
            });

            var script = generator.GenerateMigrationScript(plan, report.SourceIdentifier, report.TargetIdentifier);
            return Results.Ok(new { Script = script });
        });

        var url = $"http://localhost:{port}";
        Console.WriteLine($"\n[SQLDrift Web UI] Live server started at: {url}");
        Console.WriteLine("Press Ctrl+C to terminate the UI server.\n");

        if (openBrowser)
        {
            OpenBrowser(url);
        }

        await app.RunAsync(cancellationToken);
    }

    private static string LoadEmbeddedHtml()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("index.html", StringComparison.OrdinalIgnoreCase));

        if (resourceName != null)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
        }

        // Fallback if not found in manifest
        var fallbackPath = Path.Combine(AppContext.BaseDirectory, "Assets", "index.html");
        if (File.Exists(fallbackPath))
        {
            return File.ReadAllText(fallbackPath);
        }

        return "<html><body><h1>SQLDrift UI Error</h1><p>Embedded index.html not found.</p></body></html>";
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }
        catch
        {
            // Silently ignore browser launch errors (e.g. headless environment)
        }
    }
}
