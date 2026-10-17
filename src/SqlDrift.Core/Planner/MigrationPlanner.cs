using SqlDrift.Core.Models;

namespace SqlDrift.Core.Planner;

public record MigrationPlan(
    List<MigrationStep> Steps,
    List<string> CircularDependenciesDetected,
    bool ContainsBreakingChanges
);

public class MigrationPlanner
{
    public MigrationPlan BuildPlan(
        SchemaDriftReport report,
        DatabaseSchemaSnapshot source,
        DatabaseSchemaSnapshot target)
    {
        var steps = new List<MigrationStep>();
        var circularDeps = new List<string>();

        // Phase 1: Create Schemas if any are new
        var allSchemas = source.Tables.Values.Select(t => t.Schema)
            .Concat(source.ProgrammableObjects.Values.Select(p => p.Schema))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(s => !s.Equals("dbo", StringComparison.OrdinalIgnoreCase) && !s.Equals("sys", StringComparison.OrdinalIgnoreCase));

        foreach (var schema in allSchemas)
        {
            var sql = $"""
                IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = N'{schema}')
                BEGIN
                    EXEC('CREATE SCHEMA [{schema}]');
                    PRINT 'Created schema [{schema}]';
                END
                """;
            steps.Add(new MigrationStep(
                $"SCHEMA_{schema}",
                DatabaseObjectType.Table,
                schema,
                schema,
                "CREATE_SCHEMA",
                1,
                sql,
                DriftSeverity.Low,
                []
            ));
        }

        // Phase 2: Tables to be Added
        // Build table dependency graph for added tables
        var addedTableDrifts = report.Drifts
            .Where(d => d.ObjectType == DatabaseObjectType.Table && d.DriftType == DriftType.Added)
            .ToList();

        var tableGraph = new DependencyGraph();
        var addedTableKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drift in addedTableDrifts)
        {
            var key = $"{drift.Schema}.{drift.Name}";
            addedTableKeys.Add(key);
            tableGraph.AddNode(key);
        }

        // Add edges based on foreign keys
        foreach (var drift in addedTableDrifts)
        {
            var key = $"{drift.Schema}.{drift.Name}";
            if (source.Tables.TryGetValue(key, out var srcTable))
            {
                foreach (var fk in srcTable.ForeignKeys)
                {
                    var refKey = $"{fk.ReferencedSchema}.{fk.ReferencedTable}";
                    // Only add edge if referenced table is also being added
                    if (addedTableKeys.Contains(refKey) && !refKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        tableGraph.AddEdge(refKey, key); // refKey must come before key
                    }
                }
            }
        }

        var (sortedTableKeys, hasCycle, cycleNodes) = tableGraph.TopologicalSort();
        if (hasCycle)
        {
            circularDeps.AddRange(cycleNodes);
            // Append any unvisited cycle nodes at the end; safe because FKs are added in a later phase!
            foreach (var node in cycleNodes)
            {
                if (!sortedTableKeys.Contains(node, StringComparer.OrdinalIgnoreCase))
                {
                    sortedTableKeys.Add(node);
                }
            }
        }

        foreach (var tableKey in sortedTableKeys)
        {
            if (source.Tables.TryGetValue(tableKey, out var table))
            {
                var sql = GenerateCreateTableScript(table);
                steps.Add(new MigrationStep(
                    $"TABLE_CREATE_{table.Schema}_{table.TableName}",
                    DatabaseObjectType.Table,
                    table.Schema,
                    table.TableName,
                    "CREATE_TABLE",
                    2,
                    sql,
                    DriftSeverity.Low,
                    []
                ));
            }
        }

        // Phase 3: Modified Tables (Columns, Primary Keys, Indexes)
        var modifiedTableDrifts = report.Drifts
            .Where(d => d.ObjectType == DatabaseObjectType.Table && d.DriftType == DriftType.Modified)
            .ToList();

        foreach (var tableDrift in modifiedTableDrifts)
        {
            var tableKey = $"{tableDrift.Schema}.{tableDrift.Name}";
            source.Tables.TryGetValue(tableKey, out var srcTable);
            target.Tables.TryGetValue(tableKey, out var tgtTable);

            if (srcTable == null || tableDrift.Children == null)
                continue;

            // Columns to Add
            foreach (var colDrift in tableDrift.Children.Where(c => c.ObjectType == DatabaseObjectType.Column && c.DriftType == DriftType.Added))
            {
                var col = srcTable.Columns.FirstOrDefault(c => c.ColumnName.Equals(colDrift.Name, StringComparison.OrdinalIgnoreCase));
                if (col != null)
                {
                    var sql = GenerateAddColumnScript(srcTable, col);
                    steps.Add(new MigrationStep(
                        $"COL_ADD_{srcTable.Schema}_{srcTable.TableName}_{col.ColumnName}",
                        DatabaseObjectType.Column,
                        srcTable.Schema,
                        col.ColumnName,
                        "ADD_COLUMN",
                        3,
                        sql,
                        colDrift.Severity,
                        [$"TABLE_CREATE_{srcTable.Schema}_{srcTable.TableName}"]
                    ));
                }
            }

            // Columns to Modify
            foreach (var colDrift in tableDrift.Children.Where(c => c.ObjectType == DatabaseObjectType.Column && c.DriftType == DriftType.Modified))
            {
                var col = srcTable.Columns.FirstOrDefault(c => c.ColumnName.Equals(colDrift.Name, StringComparison.OrdinalIgnoreCase));
                if (col != null)
                {
                    var sql = GenerateAlterColumnScript(srcTable, col);
                    steps.Add(new MigrationStep(
                        $"COL_ALTER_{srcTable.Schema}_{srcTable.TableName}_{col.ColumnName}",
                        DatabaseObjectType.Column,
                        srcTable.Schema,
                        col.ColumnName,
                        "ALTER_COLUMN",
                        3,
                        sql,
                        colDrift.Severity,
                        [$"TABLE_CREATE_{srcTable.Schema}_{srcTable.TableName}"]
                    ));
                }
            }

            // Indexes to Add
            foreach (var idxDrift in tableDrift.Children.Where(c => (c.ObjectType == DatabaseObjectType.Index || c.ObjectType == DatabaseObjectType.PrimaryKey) && c.DriftType == DriftType.Added))
            {
                var idx = srcTable.Indexes.FirstOrDefault(i => i.IndexName.Equals(idxDrift.Name, StringComparison.OrdinalIgnoreCase));
                if (idx != null)
                {
                    var sql = GenerateAddIndexScript(srcTable, idx);
                    steps.Add(new MigrationStep(
                        $"IDX_ADD_{srcTable.Schema}_{srcTable.TableName}_{idx.IndexName}",
                        idx.IsPrimaryKey ? DatabaseObjectType.PrimaryKey : DatabaseObjectType.Index,
                        srcTable.Schema,
                        idx.IndexName,
                        "ADD_INDEX",
                        3,
                        sql,
                        idxDrift.Severity,
                        [$"TABLE_CREATE_{srcTable.Schema}_{srcTable.TableName}"]
                    ));
                }
            }
        }

        // Secondary Indexes for newly Added tables
        foreach (var tableKey in addedTableKeys)
        {
            if (source.Tables.TryGetValue(tableKey, out var table))
            {
                foreach (var idx in table.Indexes.Where(i => !i.IsPrimaryKey))
                {
                    var sql = GenerateAddIndexScript(table, idx);
                    steps.Add(new MigrationStep(
                        $"IDX_ADD_{table.Schema}_{table.TableName}_{idx.IndexName}",
                        DatabaseObjectType.Index,
                        table.Schema,
                        idx.IndexName,
                        "ADD_INDEX",
                        3,
                        sql,
                        DriftSeverity.Low,
                        [$"TABLE_CREATE_{table.Schema}_{table.TableName}"]
                    ));
                }
            }
        }

        // Phase 4: Foreign Keys to Add (both from new tables and modified tables)
        foreach (var (key, srcTable) in source.Tables)
        {
            var isNewTable = addedTableKeys.Contains(key);
            var isModifiedTable = modifiedTableDrifts.Any(t => $"{t.Schema}.{t.Name}".Equals(key, StringComparison.OrdinalIgnoreCase));

            if (isNewTable)
            {
                foreach (var fk in srcTable.ForeignKeys)
                {
                    var sql = GenerateAddForeignKeyScript(fk);
                    steps.Add(new MigrationStep(
                        $"FK_ADD_{fk.ParentSchema}_{fk.ParentTable}_{fk.ConstraintName}",
                        DatabaseObjectType.ForeignKey,
                        fk.ParentSchema,
                        fk.ConstraintName,
                        "ADD_FOREIGN_KEY",
                        4,
                        sql,
                        DriftSeverity.Low,
                        [$"TABLE_CREATE_{fk.ParentSchema}_{fk.ParentTable}", $"TABLE_CREATE_{fk.ReferencedSchema}_{fk.ReferencedTable}"]
                    ));
                }
            }
            else if (isModifiedTable)
            {
                var tableDrift = modifiedTableDrifts.First(t => $"{t.Schema}.{t.Name}".Equals(key, StringComparison.OrdinalIgnoreCase));
                var addedFks = tableDrift.Children?.Where(c => c.ObjectType == DatabaseObjectType.ForeignKey && c.DriftType == DriftType.Added) ?? [];
                foreach (var fkDrift in addedFks)
                {
                    var fk = srcTable.ForeignKeys.FirstOrDefault(f => f.ConstraintName.Equals(fkDrift.Name, StringComparison.OrdinalIgnoreCase));
                    if (fk != null)
                    {
                        var sql = GenerateAddForeignKeyScript(fk);
                        steps.Add(new MigrationStep(
                            $"FK_ADD_{fk.ParentSchema}_{fk.ParentTable}_{fk.ConstraintName}",
                            DatabaseObjectType.ForeignKey,
                            fk.ParentSchema,
                            fk.ConstraintName,
                            "ADD_FOREIGN_KEY",
                            4,
                            sql,
                            DriftSeverity.Low,
                            []
                        ));
                    }
                }
            }
        }

        // Phase 5: Check Constraints to Add (both added tables and modified tables)
        foreach (var (key, srcTable) in source.Tables)
        {
            if (addedTableKeys.Contains(key))
            {
                foreach (var ck in srcTable.CheckConstraints)
                {
                    var sql = GenerateAddCheckConstraintScript(srcTable, ck);
                    steps.Add(new MigrationStep(
                        $"CK_ADD_{srcTable.Schema}_{srcTable.TableName}_{ck.ConstraintName}",
                        DatabaseObjectType.CheckConstraint,
                        srcTable.Schema,
                        ck.ConstraintName,
                        "ADD_CHECK_CONSTRAINT",
                        5,
                        sql,
                        DriftSeverity.Low,
                        []
                    ));
                }
            }
            else if (modifiedTableDrifts.Any(t => $"{t.Schema}.{t.Name}".Equals(key, StringComparison.OrdinalIgnoreCase)))
            {
                var tableDrift = modifiedTableDrifts.First(t => $"{t.Schema}.{t.Name}".Equals(key, StringComparison.OrdinalIgnoreCase));
                var addedCks = tableDrift.Children?.Where(c => c.ObjectType == DatabaseObjectType.CheckConstraint && c.DriftType == DriftType.Added) ?? [];
                foreach (var ckDrift in addedCks)
                {
                    var ck = srcTable.CheckConstraints.FirstOrDefault(c => c.ConstraintName.Equals(ckDrift.Name, StringComparison.OrdinalIgnoreCase));
                    if (ck != null)
                    {
                        var sql = GenerateAddCheckConstraintScript(srcTable, ck);
                        steps.Add(new MigrationStep(
                            $"CK_ADD_{srcTable.Schema}_{srcTable.TableName}_{ck.ConstraintName}",
                            DatabaseObjectType.CheckConstraint,
                            srcTable.Schema,
                            ck.ConstraintName,
                            "ADD_CHECK_CONSTRAINT",
                            5,
                            sql,
                            DriftSeverity.Low,
                            []
                        ));
                    }
                }
            }
        }

        // Phase 6: User Defined Functions (CREATE OR ALTER)
        var functionDrifts = report.Drifts
            .Where(d => d.ObjectType == DatabaseObjectType.UserDefinedFunction && (d.DriftType == DriftType.Added || d.DriftType == DriftType.Modified))
            .ToList();

        foreach (var drift in functionDrifts)
        {
            var key = $"{drift.Schema}.{drift.Name}";
            if (source.ProgrammableObjects.TryGetValue(key, out var func))
            {
                var sql = EnsureCreateOrAlter(func.DefinitionScript, "FUNCTION");
                steps.Add(new MigrationStep(
                    $"FUNC_{func.Schema}_{func.ObjectName}",
                    DatabaseObjectType.UserDefinedFunction,
                    func.Schema,
                    func.ObjectName,
                    "CREATE_OR_ALTER_FUNCTION",
                    6,
                    sql,
                    drift.Severity,
                    []
                ));
            }
        }

        // Phase 7: Views (CREATE OR ALTER) - topologically sorted based on view-to-view dependencies
        var viewDrifts = report.Drifts
            .Where(d => d.ObjectType == DatabaseObjectType.View && (d.DriftType == DriftType.Added || d.DriftType == DriftType.Modified))
            .ToList();

        var viewGraph = new DependencyGraph();
        var viewKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var drift in viewDrifts)
        {
            var key = $"{drift.Schema}.{drift.Name}";
            viewKeys.Add(key);
            viewGraph.AddNode(key);
        }

        foreach (var drift in viewDrifts)
        {
            var key = $"{drift.Schema}.{drift.Name}";
            if (source.ProgrammableObjects.TryGetValue(key, out var view))
            {
                var refs = ScriptDependencyFinder.FindReferencedObjects(view.DefinitionScript, view.Schema);
                foreach (var r in refs)
                {
                    if (viewKeys.Contains(r) && !r.Equals(key, StringComparison.OrdinalIgnoreCase))
                    {
                        viewGraph.AddEdge(r, key); // r must be created before key
                    }
                }
            }
        }

        var (sortedViewKeys, _, _) = viewGraph.TopologicalSort();
        foreach (var key in viewKeys)
        {
            if (!sortedViewKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                sortedViewKeys.Add(key);
        }

        foreach (var viewKey in sortedViewKeys)
        {
            if (source.ProgrammableObjects.TryGetValue(viewKey, out var view))
            {
                var drift = viewDrifts.First(d => $"{d.Schema}.{d.Name}".Equals(viewKey, StringComparison.OrdinalIgnoreCase));
                var sql = EnsureCreateOrAlter(view.DefinitionScript, "VIEW");
                steps.Add(new MigrationStep(
                    $"VIEW_{view.Schema}_{view.ObjectName}",
                    DatabaseObjectType.View,
                    view.Schema,
                    view.ObjectName,
                    "CREATE_OR_ALTER_VIEW",
                    7,
                    sql,
                    drift.Severity,
                    []
                ));
            }
        }

        // Phase 8: Stored Procedures & Triggers (CREATE OR ALTER)
        var procAndTrigDrifts = report.Drifts
            .Where(d => (d.ObjectType == DatabaseObjectType.StoredProcedure || d.ObjectType == DatabaseObjectType.Trigger) &&
                        (d.DriftType == DriftType.Added || d.DriftType == DriftType.Modified))
            .ToList();

        foreach (var drift in procAndTrigDrifts)
        {
            var key = $"{drift.Schema}.{drift.Name}";
            if (source.ProgrammableObjects.TryGetValue(key, out var obj))
            {
                var keyword = obj.ObjectType == DatabaseObjectType.Trigger ? "TRIGGER" : "PROCEDURE";
                var sql = EnsureCreateOrAlter(obj.DefinitionScript, keyword);
                steps.Add(new MigrationStep(
                    $"{keyword}_{obj.Schema}_{obj.ObjectName}",
                    obj.ObjectType,
                    obj.Schema,
                    obj.ObjectName,
                    $"CREATE_OR_ALTER_{keyword}",
                    8,
                    sql,
                    drift.Severity,
                    []
                ));
            }
        }

        // Phase 9: Drops (Destructive operations: Column drops, Table drops, Programmable drops)
        var missingDrifts = report.Drifts.Where(d => d.DriftType == DriftType.Missing).ToList();
        var modifiedWithMissingCols = modifiedTableDrifts
            .SelectMany(t => (t.Children ?? []).Where(c => c.DriftType == DriftType.Missing).Select(c => (Table: t, Child: c)))
            .ToList();

        foreach (var (t, c) in modifiedWithMissingCols)
        {
            var sql = $"""
                IF EXISTS (
                    SELECT 1 FROM sys.columns c 
                    JOIN sys.tables t ON c.object_id = t.object_id 
                    JOIN sys.schemas s ON t.schema_id = s.schema_id 
                    WHERE s.name = N'{t.Schema}' AND t.name = N'{t.Name}' AND c.name = N'{c.Name}'
                )
                BEGIN
                    ALTER TABLE [{t.Schema}].[{t.Name}] DROP COLUMN [{c.Name}];
                    PRINT 'Dropped column [{t.Schema}].[{t.Name}].[{c.Name}]';
                END
                """;

            steps.Add(new MigrationStep(
                $"DROP_COL_{t.Schema}_{t.Name}_{c.Name}",
                DatabaseObjectType.Column,
                t.Schema,
                c.Name,
                "DROP_COLUMN",
                9,
                sql,
                DriftSeverity.Breaking,
                []
            ));
        }

        // Drop missing Foreign Keys
        var modifiedWithMissingFks = modifiedTableDrifts
            .SelectMany(t => (t.Children ?? []).Where(c => c.ObjectType == DatabaseObjectType.ForeignKey && c.DriftType == DriftType.Missing).Select(c => (Table: t, Child: c)))
            .ToList();

        foreach (var (t, c) in modifiedWithMissingFks)
        {
            var sql = $"""
                IF EXISTS (
                    SELECT 1 FROM sys.foreign_keys fk
                    JOIN sys.tables t ON fk.parent_object_id = t.object_id
                    JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE s.name = N'{t.Schema}' AND t.name = N'{t.Name}' AND fk.name = N'{c.Name}'
                )
                BEGIN
                    ALTER TABLE [{t.Schema}].[{t.Name}] DROP CONSTRAINT [{c.Name}];
                    PRINT 'Dropped foreign key [{t.Schema}].[{t.Name}].[{c.Name}]';
                END
                """;

            steps.Add(new MigrationStep(
                $"DROP_FK_{t.Schema}_{t.Name}_{c.Name}",
                DatabaseObjectType.ForeignKey,
                t.Schema,
                c.Name,
                "DROP_FOREIGN_KEY",
                9,
                sql,
                DriftSeverity.Breaking,
                []
            ));
        }

        // Drop missing Check Constraints
        var modifiedWithMissingCks = modifiedTableDrifts
            .SelectMany(t => (t.Children ?? []).Where(c => c.ObjectType == DatabaseObjectType.CheckConstraint && c.DriftType == DriftType.Missing).Select(c => (Table: t, Child: c)))
            .ToList();

        foreach (var (t, c) in modifiedWithMissingCks)
        {
            var sql = $"""
                IF EXISTS (
                    SELECT 1 FROM sys.check_constraints cc
                    JOIN sys.tables t ON cc.parent_object_id = t.object_id
                    JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE s.name = N'{t.Schema}' AND t.name = N'{t.Name}' AND cc.name = N'{c.Name}'
                )
                BEGIN
                    ALTER TABLE [{t.Schema}].[{t.Name}] DROP CONSTRAINT [{c.Name}];
                    PRINT 'Dropped check constraint [{t.Schema}].[{t.Name}].[{c.Name}]';
                END
                """;

            steps.Add(new MigrationStep(
                $"DROP_CK_{t.Schema}_{t.Name}_{c.Name}",
                DatabaseObjectType.CheckConstraint,
                t.Schema,
                c.Name,
                "DROP_CHECK_CONSTRAINT",
                9,
                sql,
                DriftSeverity.Breaking,
                []
            ));
        }

        // Drop missing Indexes
        var modifiedWithMissingIdxs = modifiedTableDrifts
            .SelectMany(t => (t.Children ?? []).Where(c => c.ObjectType == DatabaseObjectType.Index && c.DriftType == DriftType.Missing).Select(c => (Table: t, Child: c)))
            .ToList();

        foreach (var (t, c) in modifiedWithMissingIdxs)
        {
            var sql = $"""
                IF EXISTS (
                    SELECT 1 FROM sys.indexes i
                    JOIN sys.tables t ON i.object_id = t.object_id
                    JOIN sys.schemas s ON t.schema_id = s.schema_id
                    WHERE s.name = N'{t.Schema}' AND t.name = N'{t.Name}' AND i.name = N'{c.Name}'
                )
                BEGIN
                    DROP INDEX [{c.Name}] ON [{t.Schema}].[{t.Name}];
                    PRINT 'Dropped index [{t.Schema}].[{t.Name}].[{c.Name}]';
                END
                """;

            steps.Add(new MigrationStep(
                $"DROP_IDX_{t.Schema}_{t.Name}_{c.Name}",
                DatabaseObjectType.Index,
                t.Schema,
                c.Name,
                "DROP_INDEX",
                9,
                sql,
                c.Severity,
                []
            ));
        }

        foreach (var drift in missingDrifts)
        {
            var sql = drift.ObjectType switch
            {
                DatabaseObjectType.Table => $"""
                    IF EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = N'{drift.Schema}' AND t.name = N'{drift.Name}')
                    BEGIN
                        DROP TABLE [{drift.Schema}].[{drift.Name}];
                        PRINT 'Dropped table [{drift.Schema}].[{drift.Name}]';
                    END
                    """,
                DatabaseObjectType.View => $"""
                    IF EXISTS (SELECT 1 FROM sys.views v JOIN sys.schemas s ON v.schema_id = s.schema_id WHERE s.name = N'{drift.Schema}' AND v.name = N'{drift.Name}')
                    BEGIN
                        DROP VIEW [{drift.Schema}].[{drift.Name}];
                        PRINT 'Dropped view [{drift.Schema}].[{drift.Name}]';
                    END
                    """,
                DatabaseObjectType.StoredProcedure => $"""
                    IF EXISTS (SELECT 1 FROM sys.procedures p JOIN sys.schemas s ON p.schema_id = s.schema_id WHERE s.name = N'{drift.Schema}' AND p.name = N'{drift.Name}')
                    BEGIN
                        DROP PROCEDURE [{drift.Schema}].[{drift.Name}];
                        PRINT 'Dropped procedure [{drift.Schema}].[{drift.Name}]';
                    END
                    """,
                DatabaseObjectType.UserDefinedFunction => $"""
                    IF EXISTS (SELECT 1 FROM sys.objects o JOIN sys.schemas s ON o.schema_id = s.schema_id WHERE s.name = N'{drift.Schema}' AND o.name = N'{drift.Name}' AND o.type IN ('FN', 'IF', 'TF'))
                    BEGIN
                        DROP FUNCTION [{drift.Schema}].[{drift.Name}];
                        PRINT 'Dropped function [{drift.Schema}].[{drift.Name}]';
                    END
                    """,
                _ => $"-- DROP {drift.ObjectType} [{drift.Schema}].[{drift.Name}]"
            };

            steps.Add(new MigrationStep(
                $"DROP_{drift.ObjectType.ToString().ToUpperInvariant()}_{drift.Schema}_{drift.Name}",
                drift.ObjectType,
                drift.Schema,
                drift.Name,
                $"DROP_{drift.ObjectType.ToString().ToUpperInvariant()}",
                9,
                sql,
                drift.Severity,
                []
            ));
        }

        var containsBreaking = steps.Any(s => s.Severity == DriftSeverity.Breaking);
        return new MigrationPlan(steps.OrderBy(s => s.Phase).ToList(), circularDeps, containsBreaking);
    }

    private static string GenerateCreateTableScript(TableModel table)
    {
        var colDefs = new List<string>();
        foreach (var c in table.Columns)
        {
            var identity = c.IsIdentity ? " IDENTITY(1,1)" : "";
            var nullability = c.IsNullable ? "NULL" : "NOT NULL";
            var def = !string.IsNullOrEmpty(c.DefaultValue) ? $" DEFAULT {c.DefaultValue}" : "";
            colDefs.Add($"    [{c.ColumnName}] {c.FormattedDataType}{identity} {nullability}{def}");
        }

        // Primary Key constraint
        var pk = table.Indexes.FirstOrDefault(i => i.IsPrimaryKey);
        if (pk != null)
        {
            var clustered = pk.IsClustered ? "CLUSTERED" : "NONCLUSTERED";
            var keys = string.Join(", ", pk.KeyColumns.Select(k => $"[{k}]"));
            colDefs.Add($"    CONSTRAINT [{pk.IndexName}] PRIMARY KEY {clustered} ({keys})");
        }

        var columnsBlock = string.Join(",\n", colDefs);

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.tables t 
                JOIN sys.schemas s ON t.schema_id = s.schema_id 
                WHERE s.name = N'{table.Schema}' AND t.name = N'{table.TableName}'
            )
            BEGIN
                CREATE TABLE [{table.Schema}].[{table.TableName}] (
            {columnsBlock}
                );
                PRINT 'Created table [{table.Schema}].[{table.TableName}]';
            END
            """;
    }

    private static string GenerateAddColumnScript(TableModel table, ColumnModel col)
    {
        var identity = col.IsIdentity ? " IDENTITY(1,1)" : "";
        var nullability = col.IsNullable ? "NULL" : "NOT NULL";
        var def = !string.IsNullOrEmpty(col.DefaultValue) ? $" DEFAULT {col.DefaultValue}" : "";

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns c 
                JOIN sys.tables t ON c.object_id = t.object_id 
                JOIN sys.schemas s ON t.schema_id = s.schema_id 
                WHERE s.name = N'{table.Schema}' AND t.name = N'{table.TableName}' AND c.name = N'{col.ColumnName}'
            )
            BEGIN
                ALTER TABLE [{table.Schema}].[{table.TableName}] ADD [{col.ColumnName}] {col.FormattedDataType}{identity} {nullability}{def};
                PRINT 'Added column [{table.Schema}].[{table.TableName}].[{col.ColumnName}]';
            END
            """;
    }

    private static string GenerateAlterColumnScript(TableModel table, ColumnModel col)
    {
        var nullability = col.IsNullable ? "NULL" : "NOT NULL";
        return $"""
            ALTER TABLE [{table.Schema}].[{table.TableName}] ALTER COLUMN [{col.ColumnName}] {col.FormattedDataType} {nullability};
            PRINT 'Altered column [{table.Schema}].[{table.TableName}].[{col.ColumnName}] to {col.FormattedDataType} {nullability}';
            """;
    }

    private static string GenerateAddIndexScript(TableModel table, IndexModel idx)
    {
        var unique = idx.IsUnique ? "UNIQUE " : "";
        var clustered = idx.IsClustered ? "CLUSTERED" : "NONCLUSTERED";
        var keys = string.Join(", ", idx.KeyColumns.Select(k => $"[{k}]"));
        var incs = idx.IncludedColumns.Count > 0 ? $" INCLUDE ({string.Join(", ", idx.IncludedColumns.Select(i => $"[{i}]"))})" : "";
        var filter = !string.IsNullOrEmpty(idx.FilterDefinition) ? $" WHERE {idx.FilterDefinition}" : "";

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.indexes i 
                JOIN sys.tables t ON i.object_id = t.object_id 
                JOIN sys.schemas s ON t.schema_id = s.schema_id 
                WHERE s.name = N'{table.Schema}' AND t.name = N'{table.TableName}' AND i.name = N'{idx.IndexName}'
            )
            BEGIN
                CREATE {unique}{clustered} INDEX [{idx.IndexName}] ON [{table.Schema}].[{table.TableName}] ({keys}){incs}{filter};
                PRINT 'Created index [{idx.IndexName}] on [{table.Schema}].[{table.TableName}]';
            END
            """;
    }

    private static string GenerateAddForeignKeyScript(ForeignKeyModel fk)
    {
        var parentCols = string.Join(", ", fk.ParentColumns.Select(c => $"[{c}]"));
        var refCols = string.Join(", ", fk.ReferencedColumns.Select(c => $"[{c}]"));
        var onDel = !string.IsNullOrWhiteSpace(fk.OnDeleteAction) &&
                    !fk.OnDeleteAction.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase) &&
                    !fk.OnDeleteAction.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase) &&
                    !fk.OnDeleteAction.Equals("NOTSPECIFIED", StringComparison.OrdinalIgnoreCase)
            ? $" ON DELETE {fk.OnDeleteAction.Replace('_', ' ')}"
            : "";
        var onUpd = !string.IsNullOrWhiteSpace(fk.OnUpdateAction) &&
                    !fk.OnUpdateAction.Equals("NO_ACTION", StringComparison.OrdinalIgnoreCase) &&
                    !fk.OnUpdateAction.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase) &&
                    !fk.OnUpdateAction.Equals("NOTSPECIFIED", StringComparison.OrdinalIgnoreCase)
            ? $" ON UPDATE {fk.OnUpdateAction.Replace('_', ' ')}"
            : "";

        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.foreign_keys fk 
                JOIN sys.tables t ON fk.parent_object_id = t.object_id 
                JOIN sys.schemas s ON t.schema_id = s.schema_id 
                WHERE s.name = N'{fk.ParentSchema}' AND t.name = N'{fk.ParentTable}' AND fk.name = N'{fk.ConstraintName}'
            )
            BEGIN
                ALTER TABLE [{fk.ParentSchema}].[{fk.ParentTable}] 
                ADD CONSTRAINT [{fk.ConstraintName}] FOREIGN KEY ({parentCols}) 
                REFERENCES [{fk.ReferencedSchema}].[{fk.ReferencedTable}] ({refCols}){onDel}{onUpd};
                PRINT 'Added foreign key [{fk.ConstraintName}]';
            END
            """;
    }

    private static string GenerateAddCheckConstraintScript(TableModel table, CheckConstraintModel ck)
    {
        return $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.check_constraints cc 
                JOIN sys.tables t ON cc.parent_object_id = t.object_id 
                JOIN sys.schemas s ON t.schema_id = s.schema_id 
                WHERE s.name = N'{table.Schema}' AND t.name = N'{table.TableName}' AND cc.name = N'{ck.ConstraintName}'
            )
            BEGIN
                ALTER TABLE [{table.Schema}].[{table.TableName}] 
                ADD CONSTRAINT [{ck.ConstraintName}] CHECK {ck.Definition};
                PRINT 'Added check constraint [{ck.ConstraintName}]';
            END
            """;
    }

    private static string EnsureCreateOrAlter(string script, string keyword)
    {
        var trimmed = script.Trim();
        var pattern = $@"\b(CREATE\s+OR\s+ALTER|CREATE|ALTER)\s+{keyword}\b";
        if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
        {
            return System.Text.RegularExpressions.Regex.Replace(
                trimmed,
                pattern,
                $"CREATE OR ALTER {keyword}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        return trimmed;
    }
}
