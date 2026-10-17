using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using SqlDrift.Core.Models;
using SqlDrift.Core.Reporting;
using Xunit;

namespace SqlDrift.Core.Tests;

public class ReportFormattersTests
{
    private static SchemaDriftReport CreateSampleReport()
    {
        return new SchemaDriftReport(
            "SourceDb",
            "TargetDb",
            DateTime.UtcNow,
            [
                new ObjectDriftResult(
                    DatabaseObjectType.Table,
                    "dbo",
                    "Orders",
                    DriftType.Added,
                    DriftSeverity.Low,
                    "Table [dbo].[Orders] is present in source but missing in target."
                ),
                new ObjectDriftResult(
                    DatabaseObjectType.Column,
                    "dbo",
                    "Amount",
                    DriftType.Modified,
                    DriftSeverity.Breaking,
                    "Column [Amount] type narrowed from decimal(18,2) to decimal(10,2)."
                )
            ]
        );
    }

    [Fact]
    public void ToJson_Should_Produce_Valid_Json()
    {
        var report = CreateSampleReport();
        var json = ReportFormatters.ToJson(report);

        json.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("sourceIdentifier").GetString().Should().Be("SourceDb");
        doc.RootElement.GetProperty("totalDrifts").GetInt32().Should().Be(2);
        doc.RootElement.GetProperty("breakingCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public void ToMarkdown_Should_Contain_Summary_Metrics_And_Breakdown()
    {
        var report = CreateSampleReport();
        var md = ReportFormatters.ToMarkdown(report);

        md.Should().Contain("# 🔍 SQLDrift Schema Drift Report");
        md.Should().Contain("| **Total Drifts** | `2` |");
        md.Should().Contain("🚨 **Breaking**");
        md.Should().Contain("Orders");
        md.Should().Contain("Amount");
    }

    [Fact]
    public void ToJUnitXml_Should_Produce_Valid_Xml_With_Failures()
    {
        var report = CreateSampleReport();
        var xml = ReportFormatters.ToJUnitXml(report);

        xml.Should().NotBeNullOrWhiteSpace();

        var doc = XDocument.Parse(xml);
        doc.Root.Should().NotBeNull();
        doc.Root!.Name.LocalName.Should().Be("testsuites");
        doc.Root.Attribute("tests")?.Value.Should().Be("2");
        doc.Root.Attribute("failures")?.Value.Should().Be("1");

        var testcases = doc.Descendants("testcase").ToList();
        testcases.Should().HaveCount(2);

        var failure = doc.Descendants("failure").FirstOrDefault();
        failure.Should().NotBeNull();
        failure!.Attribute("type")?.Value.Should().Be("BreakingDrift");
    }
}
