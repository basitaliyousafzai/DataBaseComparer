using FluentAssertions;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Models;
using Xunit;

namespace SqlDrift.Core.Tests;

public class SchemaDifferTests
{
    private readonly SchemaDiffer _differ = new();

    [Fact]
    public void Compare_Should_Report_Zero_Drift_For_Identical_Schemas()
    {
        var table = new TableModel(
            "dbo",
            "Products",
            [
                new ColumnModel("Id", "int", 4, 10, 0, false, true),
                new ColumnModel("Name", "nvarchar", 100, 0, 0, false, false)
            ],
            [
                new IndexModel("PK_Products", true, true, true, null, ["Id"], [])
            ],
            []
        );

        var source = new DatabaseSchemaSnapshot("Source", DateTime.UtcNow, new() { ["dbo.Products"] = table }, new());
        var target = new DatabaseSchemaSnapshot("Target", DateTime.UtcNow, new() { ["dbo.Products"] = table }, new());

        var report = _differ.Compare(source, target);

        report.HasDrift.Should().BeFalse();
        report.TotalDrifts.Should().Be(0);
        report.BreakingCount.Should().Be(0);
    }

    [Fact]
    public void Compare_Should_Detect_Added_And_Missing_Tables()
    {
        var tableA = new TableModel("dbo", "TableA", [new ColumnModel("Id", "int", 4, 10, 0, false, false)], [], []);
        var tableB = new TableModel("dbo", "TableB", [new ColumnModel("Id", "int", 4, 10, 0, false, false)], [], []);

        // Source has TableA, Target has TableB
        var source = new DatabaseSchemaSnapshot("Source", DateTime.UtcNow, new() { ["dbo.TableA"] = tableA }, new());
        var target = new DatabaseSchemaSnapshot("Target", DateTime.UtcNow, new() { ["dbo.TableB"] = tableB }, new());

        var report = _differ.Compare(source, target);

        report.HasDrift.Should().BeTrue();
        report.TotalDrifts.Should().Be(2);

        var added = report.Drifts.First(d => d.Name == "TableA");
        added.DriftType.Should().Be(DriftType.Added);
        added.Severity.Should().Be(DriftSeverity.Low);

        var missing = report.Drifts.First(d => d.Name == "TableB");
        missing.DriftType.Should().Be(DriftType.Missing);
        missing.Severity.Should().Be(DriftSeverity.Breaking);
    }

    [Fact]
    public void Compare_Should_Detect_Column_Drift_And_Severity()
    {
        var srcTable = new TableModel(
            "dbo",
            "Customers",
            [
                new ColumnModel("Id", "int", 4, 10, 0, false, true),
                new ColumnModel("Email", "nvarchar", 200, 0, 0, true, false), // Nullable addition -> Low
                new ColumnModel("Status", "varchar", 20, 0, 0, false, false), // Widened from varchar(10) to varchar(20) -> Medium
                new ColumnModel("RequiredCode", "nvarchar", 50, 0, 0, false, false) // NOT NULL with no default -> Breaking
            ],
            [],
            []
        );

        var tgtTable = new TableModel(
            "dbo",
            "Customers",
            [
                new ColumnModel("Id", "int", 4, 10, 0, false, true),
                new ColumnModel("Status", "varchar", 10, 0, 0, false, false),
                new ColumnModel("LegacyNotes", "nvarchar", 500, 0, 0, true, false) // Missing in source -> Candidate for DROP (Breaking)
            ],
            [],
            []
        );

        var source = new DatabaseSchemaSnapshot("Source", DateTime.UtcNow, new() { ["dbo.Customers"] = srcTable }, new());
        var target = new DatabaseSchemaSnapshot("Target", DateTime.UtcNow, new() { ["dbo.Customers"] = tgtTable }, new());

        var report = _differ.Compare(source, target);

        report.HasDrift.Should().BeTrue();
        report.Drifts.Should().ContainSingle();

        var tableDrift = report.Drifts.First();
        tableDrift.DriftType.Should().Be(DriftType.Modified);
        tableDrift.Children.Should().NotBeNull();
        tableDrift.Children.Should().HaveCount(4);

        // Safe nullable addition
        var emailDrift = tableDrift.Children!.First(c => c.Name == "Email");
        emailDrift.DriftType.Should().Be(DriftType.Added);
        emailDrift.Severity.Should().Be(DriftSeverity.Low);

        // Widening modification
        var statusDrift = tableDrift.Children!.First(c => c.Name == "Status");
        statusDrift.DriftType.Should().Be(DriftType.Modified);
        statusDrift.Severity.Should().Be(DriftSeverity.Medium);

        // Breaking NOT NULL addition without default
        var reqDrift = tableDrift.Children!.First(c => c.Name == "RequiredCode");
        reqDrift.DriftType.Should().Be(DriftType.Added);
        reqDrift.Severity.Should().Be(DriftSeverity.Breaking);

        // Breaking column drop candidate
        var dropDrift = tableDrift.Children!.First(c => c.Name == "LegacyNotes");
        dropDrift.DriftType.Should().Be(DriftType.Missing);
        dropDrift.Severity.Should().Be(DriftSeverity.Breaking);
    }

    [Fact]
    public void Compare_Should_Detect_Programmable_Object_Modification()
    {
        var procSrc = new ProgrammableObjectModel(
            DatabaseObjectType.StoredProcedure,
            "dbo",
            "usp_Report",
            "CREATE PROCEDURE [dbo].[usp_Report] AS SELECT 1, 2;",
            "HASH1"
        );

        var procTgt = new ProgrammableObjectModel(
            DatabaseObjectType.StoredProcedure,
            "dbo",
            "usp_Report",
            "CREATE PROCEDURE [dbo].[usp_Report] AS SELECT 1;",
            "HASH2"
        );

        var source = new DatabaseSchemaSnapshot("Source", DateTime.UtcNow, new(), new() { ["dbo.usp_Report"] = procSrc });
        var target = new DatabaseSchemaSnapshot("Target", DateTime.UtcNow, new(), new() { ["dbo.usp_Report"] = procTgt });

        var report = _differ.Compare(source, target);

        report.HasDrift.Should().BeTrue();
        report.Drifts.Should().ContainSingle();

        var drift = report.Drifts.First();
        drift.Name.Should().Be("usp_Report");
        drift.DriftType.Should().Be(DriftType.Modified);
        drift.Severity.Should().Be(DriftSeverity.Medium);
    }
}
