using System.Text;
using SqlDrift.Core.Diff;
using SqlDrift.Core.Models;
using SqlDrift.Core.Planner;

namespace SqlDrift.Core.Generator;

public class GeneratorOptions
{
    public bool AllowDataLoss { get; set; } = false;
    public bool WrapInTransaction { get; set; } = true;
    public string TransactionName { get; set; } = "SqlDrift_Migration";
    public bool IncludeHeader { get; set; } = true;
}

public class MigrationScriptGenerator
{
    private readonly GeneratorOptions _options;

    public MigrationScriptGenerator(GeneratorOptions? options = null)
    {
        _options = options ?? new GeneratorOptions();
    }

    public string GenerateMigrationScript(MigrationPlan plan, string sourceName = "Source", string targetName = "Target")
    {
        var sb = new StringBuilder();

        if (_options.IncludeHeader)
        {
            sb.AppendLine("/*");
            sb.AppendLine("===============================================================================");
            sb.AppendLine($" SQLDrift Migration Script");
            sb.AppendLine($" Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
            sb.AppendLine($" Source:    {sourceName}");
            sb.AppendLine($" Target:    {targetName}");
            sb.AppendLine($" Steps:     {plan.Steps.Count}");
            sb.AppendLine($" Breaking:  {plan.ContainsBreakingChanges} (AllowDataLoss={_options.AllowDataLoss})");
            sb.AppendLine("===============================================================================");
            sb.AppendLine("*/");
            sb.AppendLine();
        }

        sb.AppendLine("SET XACT_ABORT ON;");
        sb.AppendLine("SET NOCOUNT ON;");
        sb.AppendLine();

        if (_options.WrapInTransaction)
        {
            sb.AppendLine("BEGIN TRY");
            sb.AppendLine($"    BEGIN TRANSACTION [{_options.TransactionName}];");
            sb.AppendLine();
        }

        var indent = _options.WrapInTransaction ? "    " : "";

        // Check if breaking changes exist and data loss is not allowed
        if (plan.ContainsBreakingChanges && !_options.AllowDataLoss)
        {
            sb.AppendLine($"{indent}-- WARNING: The migration plan contains BREAKING changes or potential DATA LOSS.");
            sb.AppendLine($"{indent}-- Re-run with --allow-data-loss to include active execution of destructive steps.");
            sb.AppendLine();
        }

        foreach (var step in plan.Steps)
        {
            var isDestructive = step.Severity == DriftSeverity.Breaking && step.Action.StartsWith("DROP");

            sb.AppendLine($"{indent}-- Step {step.Phase}.{step.Id}: {step.Action} on {step.ObjectType} [{step.Schema}].[{step.Name}] (Severity: {step.Severity})");

            if (isDestructive && !_options.AllowDataLoss)
            {
                sb.AppendLine($"{indent}/* [DATA LOSS GUARD: OMITTED UNLESS --allow-data-loss]");
                foreach (var line in step.SqlScript.Split('\n'))
                {
                    sb.AppendLine($"{indent}{line.TrimEnd()}");
                }
                sb.AppendLine($"{indent}*/");
                sb.AppendLine($"{indent}PRINT 'SKIPPED DESTRUCTIVE STEP: {step.Id} (Data loss not permitted)';");
            }
            else
            {
                // If it's a programmable object (Function, View, Procedure, Trigger), wrap in EXEC('...') for transaction compatibility
                if (step.Action.StartsWith("CREATE_OR_ALTER"))
                {
                    var escapedScript = step.SqlScript.Replace("'", "''");
                    sb.AppendLine($"{indent}EXEC(N'{escapedScript}');");
                    sb.AppendLine($"{indent}PRINT 'Updated {step.ObjectType} [{step.Schema}].[{step.Name}]';");
                }
                else
                {
                    foreach (var line in step.SqlScript.Split('\n'))
                    {
                        sb.AppendLine($"{indent}{line.TrimEnd()}");
                    }
                }
            }

            sb.AppendLine();
        }

        if (_options.WrapInTransaction)
        {
            sb.AppendLine($"{indent}COMMIT TRANSACTION [{_options.TransactionName}];");
            sb.AppendLine($"{indent}PRINT '==================================================';");
            sb.AppendLine($"{indent}PRINT 'SQLDrift: Migration completed successfully.';");
            sb.AppendLine($"{indent}PRINT '==================================================';");
            sb.AppendLine("END TRY");
            sb.AppendLine("BEGIN CATCH");
            sb.AppendLine($"    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION [{_options.TransactionName}];");
            sb.AppendLine("    DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();");
            sb.AppendLine("    DECLARE @ErrSev INT = ERROR_SEVERITY();");
            sb.AppendLine("    DECLARE @ErrState INT = ERROR_STATE();");
            sb.AppendLine("    RAISERROR(@ErrMsg, @ErrSev, @ErrState);");
            sb.AppendLine("END CATCH;");
        }

        return sb.ToString();
    }

    public string GenerateRollbackScript(
        SchemaDriftReport report,
        DatabaseSchemaSnapshot source,
        DatabaseSchemaSnapshot target)
    {
        // Reversal is comparing target against source (inverting direction)
        var differ = new SchemaDiffer();
        var invertedReport = differ.Compare(target, source, report.TargetIdentifier, report.SourceIdentifier);
        var planner = new MigrationPlanner();
        var rollbackPlan = planner.BuildPlan(invertedReport, target, source);

        var rollbackGen = new MigrationScriptGenerator(new GeneratorOptions
        {
            AllowDataLoss = _options.AllowDataLoss,
            WrapInTransaction = _options.WrapInTransaction,
            TransactionName = "SqlDrift_Rollback",
            IncludeHeader = true
        });

        return rollbackGen.GenerateMigrationScript(rollbackPlan, report.TargetIdentifier, report.SourceIdentifier);
    }
}
