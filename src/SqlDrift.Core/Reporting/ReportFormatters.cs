using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SqlDrift.Core.Models;

namespace SqlDrift.Core.Reporting;

public static class ReportFormatters
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ToJson(SchemaDriftReport report)
    {
        return JsonSerializer.Serialize(report, JsonOpts);
    }

    public static string ToMarkdown(SchemaDriftReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 🔍 SQLDrift Schema Drift Report");
        sb.AppendLine();
        sb.AppendLine($"- **Analyzed At:** `{report.AnalyzedAtUtc:yyyy-MM-dd HH:mm:ss} UTC`");
        sb.AppendLine($"- **Source:** `{report.SourceIdentifier}`");
        sb.AppendLine($"- **Target:** `{report.TargetIdentifier}`");
        sb.AppendLine($"- **Status:** {(report.HasDrift ? (report.HasBreakingDrift ? "🚨 **Breaking Drift Detected**" : "⚠️ **Drift Detected**") : "✅ **In Sync**")}");
        sb.AppendLine();

        sb.AppendLine("### Summary Metrics");
        sb.AppendLine("| Metric | Count |");
        sb.AppendLine("| :--- | :--- |");
        sb.AppendLine($"| **Total Drifts** | `{report.TotalDrifts}` |");
        sb.AppendLine($"| 🚨 **Breaking** | `{report.BreakingCount}` |");
        sb.AppendLine($"| ⚠️ **Medium** | `{report.MediumCount}` |");
        sb.AppendLine($"| ℹ️ **Low** | `{report.LowCount}` |");
        sb.AppendLine($"| ➕ **Added** | `{report.AddedCount}` |");
        sb.AppendLine($"| ➖ **Missing** | `{report.MissingCount}` |");
        sb.AppendLine($"| ✏️ **Modified** | `{report.ModifiedCount}` |");
        sb.AppendLine();

        if (report.Drifts.Count > 0)
        {
            sb.AppendLine("### Drift Breakdown");
            sb.AppendLine("| Type | Object | Severity | Drift | Description |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");

            foreach (var d in report.Drifts)
            {
                var sevBadge = d.Severity switch
                {
                    DriftSeverity.Breaking => "🔴 Breaking",
                    DriftSeverity.Medium => "🟡 Medium",
                    DriftSeverity.Low => "🟢 Low",
                    _ => "⚪ None"
                };

                var driftTypeBadge = d.DriftType switch
                {
                    DriftType.Added => "`Added`",
                    DriftType.Missing => "`Missing`",
                    DriftType.Modified => "`Modified`",
                    _ => "`Unchanged`"
                };

                sb.AppendLine($"| `{d.ObjectType}` | `{d.FullName}` | {sevBadge} | {driftTypeBadge} | {SecurityElement.Escape(d.Description)} |");

                if (d.Children != null && d.Children.Count > 0)
                {
                    foreach (var child in d.Children)
                    {
                        var childSev = child.Severity switch
                        {
                            DriftSeverity.Breaking => "🔴 Breaking",
                            DriftSeverity.Medium => "🟡 Medium",
                            DriftSeverity.Low => "🟢 Low",
                            _ => "⚪ None"
                        };
                        sb.AppendLine($"| ↳ `{child.ObjectType}` | `..{child.Name}` | {childSev} | `{child.DriftType}` | {SecurityElement.Escape(child.Description)} |");
                    }
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string ToJUnitXml(SchemaDriftReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine($"<testsuites name=\"SQLDrift\" time=\"0\" tests=\"{report.TotalDrifts}\" failures=\"{report.BreakingCount}\" errors=\"0\">");
        sb.AppendLine($"  <testsuite name=\"SchemaDrift\" tests=\"{report.TotalDrifts}\" failures=\"{report.BreakingCount}\" errors=\"0\" time=\"0\">");

        foreach (var drift in report.Drifts)
        {
            var className = SecurityElement.Escape($"{drift.ObjectType}.{drift.Schema}");
            var testName = SecurityElement.Escape(drift.Name);

            sb.AppendLine($"    <testcase classname=\"{className}\" name=\"{testName}\" time=\"0\">");
            if (drift.Severity == DriftSeverity.Breaking)
            {
                var message = SecurityElement.Escape(drift.Description);
                sb.AppendLine($"      <failure message=\"{message}\" type=\"BreakingDrift\">{message}</failure>");
            }
            else if (drift.DriftType != DriftType.Unchanged)
            {
                var message = SecurityElement.Escape(drift.Description);
                sb.AppendLine($"      <system-out>{message}</system-out>");
            }
            sb.AppendLine("    </testcase>");
        }

        sb.AppendLine("  </testsuite>");
        sb.AppendLine("</testsuites>");
        return sb.ToString();
    }
}
