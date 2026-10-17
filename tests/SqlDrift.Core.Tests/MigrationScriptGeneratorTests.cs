using FluentAssertions;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Generator;
using SqlDrift.Core.Models;
using SqlDrift.Core.Planner;
using Xunit;

namespace SqlDrift.Core.Tests;

public class MigrationScriptGeneratorTests
{
    private readonly MigrationPlanner _planner = new();
    private readonly SchemaDiffer _differ = new();

    [Fact]
    public void GenerateMigrationScript_Should_Wrap_In_Safe_Transaction_Block()
    {
        var sourceTable = new TableModel(
            "dbo",
            "AuditLogs",
            [
                new ColumnModel("Id", "bigint", 8, 19, 0, false, true),
                new ColumnModel("Message", "nvarchar", -1, 0, 0, false, false)
            ],
            [new IndexModel("PK_AuditLogs", true, true, true, null, ["Id"], [])],
            []
        );

        var source = new DatabaseSchemaSnapshot("Source", DateTime.UtcNow, new() { ["dbo.AuditLogs"] = sourceTable }, new());
        var target = DatabaseSchemaSnapshot.CreateEmpty("Target");

        var report = _differ.Compare(source, target);
        var plan = _planner.BuildPlan(report, source, target);

        var generator = new MigrationScriptGenerator(new GeneratorOptions
        {
            WrapInTransaction = true,
            TransactionName = "TestMigration"
        });

        var sql = generator.GenerateMigrationScript(plan, "SourceDb", "TargetDb");

        sql.Should().Contain("BEGIN TRY");
        sql.Should().Contain("BEGIN TRANSACTION [TestMigration];");
        sql.Should().Contain("COMMIT TRANSACTION [TestMigration];");
        sql.Should().Contain("END TRY");
        sql.Should().Contain("BEGIN CATCH");
        sql.Should().Contain("IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION [TestMigration];");
        sql.Should().Contain("RAISERROR(@ErrMsg, @ErrSev, @ErrState);");
        sql.Should().Contain("END CATCH;");
    }

    [Fact]
    public void GenerateMigrationScript_Should_Guard_Destructive_Operations_When_DataLoss_Not_Allowed()
    {
        // Target has a table and column that Source does NOT have (requires drops)
        var sourceTable = new TableModel(
            "dbo",
            "Employees",
            [new ColumnModel("Id", "int", 4, 10, 0, false, true)],
            [],
            []
        );

        var targetTable = new TableModel(
            "dbo",
            "Employees",
            [
                new ColumnModel("Id", "int", 4, 10, 0, false, true),
                new ColumnModel("DeprecatedColumn", "varchar", 50, 0, 0, true, false)
            ],
            [],
            []
        );

        var source = new DatabaseSchemaSnapshot("Source", DateTime.UtcNow, new() { ["dbo.Employees"] = sourceTable }, new());
        var target = new DatabaseSchemaSnapshot("Target", DateTime.UtcNow, new() { ["dbo.Employees"] = targetTable }, new());

        var report = _differ.Compare(source, target);
        var plan = _planner.BuildPlan(report, source, target);

        // Generator with AllowDataLoss = false
        var safeGenerator = new MigrationScriptGenerator(new GeneratorOptions { AllowDataLoss = false });
        var safeSql = safeGenerator.GenerateMigrationScript(plan);

        safeSql.Should().Contain("[DATA LOSS GUARD: OMITTED UNLESS --allow-data-loss]");
        safeSql.Should().Contain("SKIPPED DESTRUCTIVE STEP");

        // Generator with AllowDataLoss = true
        var forceGenerator = new MigrationScriptGenerator(new GeneratorOptions { AllowDataLoss = true });
        var forceSql = forceGenerator.GenerateMigrationScript(plan);

        forceSql.Should().NotContain("[DATA LOSS GUARD: OMITTED UNLESS --allow-data-loss]");
        forceSql.Should().Contain("ALTER TABLE [dbo].[Employees] DROP COLUMN [DeprecatedColumn];");
    }

    [Fact]
    public void GenerateRollbackScript_Should_Invert_Operations()
    {
        // Source has new column, Target does not
        var sourceTable = new TableModel(
            "dbo",
            "Customers",
            [
                new ColumnModel("Id", "int", 4, 10, 0, false, true),
                new ColumnModel("NewField", "nvarchar", 100, 0, 0, true, false)
            ],
            [],
            []
        );

        var targetTable = new TableModel(
            "dbo",
            "Customers",
            [new ColumnModel("Id", "int", 4, 10, 0, false, true)],
            [],
            []
        );

        var source = new DatabaseSchemaSnapshot("Source", DateTime.UtcNow, new() { ["dbo.Customers"] = sourceTable }, new());
        var target = new DatabaseSchemaSnapshot("Target", DateTime.UtcNow, new() { ["dbo.Customers"] = targetTable }, new());

        var report = _differ.Compare(source, target);

        var generator = new MigrationScriptGenerator(new GeneratorOptions { AllowDataLoss = true });
        var rollbackSql = generator.GenerateRollbackScript(report, source, target);

        // Rollback should drop the newly added column
        rollbackSql.Should().Contain("DROP COLUMN [NewField]");
    }
}
