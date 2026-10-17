using FluentAssertions;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Models;
using SqlDrift.Core.Planner;
using Xunit;

namespace SqlDrift.Core.Tests;

public class MigrationPlannerTests
{
    private readonly MigrationPlanner _planner = new();
    private readonly SchemaDiffer _differ = new();

    [Fact]
    public void BuildPlan_Should_Order_Phases_Correctly()
    {
        // Source has custom schema, tables with FK, and stored procedure
        var parentTable = new TableModel(
            "sales",
            "Customers",
            [new ColumnModel("Id", "int", 4, 10, 0, false, true)],
            [new IndexModel("PK_Customers", true, true, true, null, ["Id"], [])],
            []
        );

        var childTable = new TableModel(
            "sales",
            "Invoices",
            [
                new ColumnModel("InvoiceId", "int", 4, 10, 0, false, true),
                new ColumnModel("CustomerId", "int", 4, 10, 0, false, false)
            ],
            [new IndexModel("PK_Invoices", true, true, true, null, ["InvoiceId"], [])],
            [new ForeignKeyModel("FK_Invoices_Customers", "sales", "Invoices", ["CustomerId"], "sales", "Customers", ["Id"])]
        );

        var proc = new ProgrammableObjectModel(
            DatabaseObjectType.StoredProcedure,
            "sales",
            "usp_GetInvoices",
            "CREATE PROCEDURE [sales].[usp_GetInvoices] AS SELECT 1;",
            "HASH"
        );

        var source = new DatabaseSchemaSnapshot(
            "Source",
            DateTime.UtcNow,
            new() { ["sales.Customers"] = parentTable, ["sales.Invoices"] = childTable },
            new() { ["sales.usp_GetInvoices"] = proc }
        );

        // Target is completely empty
        var target = DatabaseSchemaSnapshot.CreateEmpty("Target");

        var report = _differ.Compare(source, target);
        var plan = _planner.BuildPlan(report, source, target);

        plan.Steps.Should().NotBeEmpty();

        // Check Phase ordering:
        // Phase 1 (Schema) -> Phase 2 (Tables) -> Phase 4 (Foreign Keys) -> Phase 8 (Stored Procedures)
        var phases = plan.Steps.Select(s => s.Phase).ToList();
        phases.Should().BeInAscendingOrder();

        var schemaStep = plan.Steps.First(s => s.Action == "CREATE_SCHEMA");
        schemaStep.Phase.Should().Be(1);
        schemaStep.Schema.Should().Be("sales");

        var createParentTable = plan.Steps.First(s => s.Name == "Customers" && s.Action == "CREATE_TABLE");
        createParentTable.Phase.Should().Be(2);

        var createChildTable = plan.Steps.First(s => s.Name == "Invoices" && s.Action == "CREATE_TABLE");
        createChildTable.Phase.Should().Be(2);

        var fkStep = plan.Steps.First(s => s.Action == "ADD_FOREIGN_KEY");
        fkStep.Phase.Should().Be(4);

        var procStep = plan.Steps.First(s => s.Action.StartsWith("CREATE_OR_ALTER_PROCEDURE"));
        procStep.Phase.Should().Be(8);
    }

    [Fact]
    public void BuildPlan_Should_Handle_Cyclic_Table_Dependencies()
    {
        // Table A references Table B, and Table B references Table A
        var tableA = new TableModel(
            "dbo",
            "TableA",
            [new ColumnModel("Id", "int", 4, 10, 0, false, true), new ColumnModel("BId", "int", 4, 10, 0, true, false)],
            [new IndexModel("PK_TableA", true, true, true, null, ["Id"], [])],
            [new ForeignKeyModel("FK_A_B", "dbo", "TableA", ["BId"], "dbo", "TableB", ["Id"])]
        );

        var tableB = new TableModel(
            "dbo",
            "TableB",
            [new ColumnModel("Id", "int", 4, 10, 0, false, true), new ColumnModel("AId", "int", 4, 10, 0, true, false)],
            [new IndexModel("PK_TableB", true, true, true, null, ["Id"], [])],
            [new ForeignKeyModel("FK_B_A", "dbo", "TableB", ["AId"], "dbo", "TableA", ["Id"])]
        );

        var source = new DatabaseSchemaSnapshot(
            "Source",
            DateTime.UtcNow,
            new() { ["dbo.TableA"] = tableA, ["dbo.TableB"] = tableB },
            new()
        );

        var target = DatabaseSchemaSnapshot.CreateEmpty("Target");

        var report = _differ.Compare(source, target);
        var plan = _planner.BuildPlan(report, source, target);

        // Cyclic foreign keys should be detected and decoupled
        plan.CircularDependenciesDetected.Should().NotBeEmpty();

        // Both tables should be scheduled for creation in Phase 2
        var tableSteps = plan.Steps.Where(s => s.Phase == 2 && s.Action == "CREATE_TABLE").ToList();
        tableSteps.Should().HaveCount(2);

        // Both FKs should be added in Phase 4 (after both tables exist)
        var fkSteps = plan.Steps.Where(s => s.Phase == 4 && s.Action == "ADD_FOREIGN_KEY").ToList();
        fkSteps.Should().HaveCount(2);
    }
}
