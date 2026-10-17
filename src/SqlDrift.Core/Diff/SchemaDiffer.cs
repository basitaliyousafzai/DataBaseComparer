using SqlDrift.Core.Extractor;
using SqlDrift.Core.Models;

namespace SqlDrift.Core.Diff;

public class SchemaDifferOptions
{
    public bool IgnoreCollation { get; set; } = true;
    public bool CompareConstraintNames { get; set; } = false;
}

public class SchemaDiffer
{
    private readonly SchemaDifferOptions _options;

    public SchemaDiffer(SchemaDifferOptions? options = null)
    {
        _options = options ?? new SchemaDifferOptions();
    }

    public SchemaDriftReport Compare(
        DatabaseSchemaSnapshot source,
        DatabaseSchemaSnapshot target,
        string sourceIdentifier = "Source",
        string targetIdentifier = "Target")
    {
        var drifts = new List<ObjectDriftResult>();

        // 1. Compare Tables
        foreach (var (tableKey, srcTable) in source.Tables)
        {
            if (!target.Tables.TryGetValue(tableKey, out var tgtTable))
            {
                // Table is in Source, missing in Target -> Needs to be ADDED in Target
                drifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.Table,
                    srcTable.Schema,
                    srcTable.TableName,
                    DriftType.Added,
                    SeverityEvaluator.EvaluateTableDrift(DriftType.Added),
                    $"Table [{srcTable.Schema}].[{srcTable.TableName}] is present in source but missing in target.",
                    SourceDefinition: FormatTableDefinition(srcTable),
                    TargetDefinition: null
                ));
            }
            else
            {
                // Table exists in both -> Compare table elements
                var tableChildDrifts = CompareTableElements(srcTable, tgtTable);
                if (tableChildDrifts.Count > 0)
                {
                    var maxSev = tableChildDrifts.Max(c => c.Severity);
                    drifts.Add(new ObjectDriftResult(
                        DatabaseObjectType.Table,
                        srcTable.Schema,
                        srcTable.TableName,
                        DriftType.Modified,
                        maxSev,
                        $"Table [{srcTable.Schema}].[{srcTable.TableName}] has {tableChildDrifts.Count} structural difference(s).",
                        SourceDefinition: FormatTableDefinition(srcTable),
                        TargetDefinition: FormatTableDefinition(tgtTable),
                        Children: tableChildDrifts
                    ));
                }
            }
        }

        // Tables present in Target but missing in Source -> MISSING in Source (DROP candidate)
        foreach (var (tableKey, tgtTable) in target.Tables)
        {
            if (!source.Tables.ContainsKey(tableKey))
            {
                drifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.Table,
                    tgtTable.Schema,
                    tgtTable.TableName,
                    DriftType.Missing,
                    SeverityEvaluator.EvaluateTableDrift(DriftType.Missing),
                    $"Table [{tgtTable.Schema}].[{tgtTable.TableName}] exists in target but is absent from source.",
                    SourceDefinition: null,
                    TargetDefinition: FormatTableDefinition(tgtTable)
                ));
            }
        }

        // Compare table data (records) for tables present in both
        foreach (var (tableKey, srcTable) in source.Tables)
        {
            if (!target.Tables.ContainsKey(tableKey))
                continue;

            if (!source.TableDataHashes.TryGetValue(tableKey, out var srcHash) ||
                !target.TableDataHashes.TryGetValue(tableKey, out var tgtHash))
                continue;

            if (string.Equals(srcHash, tgtHash, StringComparison.OrdinalIgnoreCase))
                continue;

            var srcCount = source.TableRowCounts.TryGetValue(tableKey, out var sc) ? sc : -1;
            var tgtCount = target.TableRowCounts.TryGetValue(tableKey, out var tc) ? tc : -1;

            drifts.Add(new ObjectDriftResult(
                DatabaseObjectType.Data,
                srcTable.Schema,
                srcTable.TableName,
                DriftType.Modified,
                DriftSeverity.Medium,
                $"Table data differs: source has {srcCount} row(s), target has {tgtCount} row(s).",
                SourceDefinition: $"Rows: {srcCount}",
                TargetDefinition: $"Rows: {tgtCount}"
            ));
        }

        // 2. Compare Programmable Objects
        foreach (var (objKey, srcObj) in source.ProgrammableObjects)
        {
            if (!target.ProgrammableObjects.TryGetValue(objKey, out var tgtObj))
            {
                // Present in source, missing in target -> ADDED
                drifts.Add(new ObjectDriftResult(
                    srcObj.ObjectType,
                    srcObj.Schema,
                    srcObj.ObjectName,
                    DriftType.Added,
                    SeverityEvaluator.EvaluateProgrammableObjectDrift(DriftType.Added, srcObj.ObjectType),
                    $"{srcObj.ObjectType} [{srcObj.Schema}].[{srcObj.ObjectName}] exists in source but is missing in target.",
                    SourceDefinition: srcObj.DefinitionScript,
                    TargetDefinition: null
                ));
            }
            else
            {
                // Exists in both -> compare scripts
                if (!AstNormalizer.AreScriptsEquivalent(srcObj.DefinitionScript, tgtObj.DefinitionScript))
                {
                    drifts.Add(new ObjectDriftResult(
                        srcObj.ObjectType,
                        srcObj.Schema,
                        srcObj.ObjectName,
                        DriftType.Modified,
                        SeverityEvaluator.EvaluateProgrammableObjectDrift(DriftType.Modified, srcObj.ObjectType),
                        $"{srcObj.ObjectType} [{srcObj.Schema}].[{srcObj.ObjectName}] definition has drifted.",
                        SourceDefinition: srcObj.DefinitionScript,
                        TargetDefinition: tgtObj.DefinitionScript
                    ));
                }
            }
        }

        // Programmable Objects in target but not in source -> MISSING
        foreach (var (objKey, tgtObj) in target.ProgrammableObjects)
        {
            if (!source.ProgrammableObjects.ContainsKey(objKey))
            {
                drifts.Add(new ObjectDriftResult(
                    tgtObj.ObjectType,
                    tgtObj.Schema,
                    tgtObj.ObjectName,
                    DriftType.Missing,
                    SeverityEvaluator.EvaluateProgrammableObjectDrift(DriftType.Missing, tgtObj.ObjectType),
                    $"{tgtObj.ObjectType} [{tgtObj.Schema}].[{tgtObj.ObjectName}] exists in target but is absent from source.",
                    SourceDefinition: null,
                    TargetDefinition: tgtObj.DefinitionScript
                ));
            }
        }

        return new SchemaDriftReport(sourceIdentifier, targetIdentifier, DateTime.UtcNow, drifts);
    }

    private List<ObjectDriftResult> CompareTableElements(TableModel src, TableModel tgt)
    {
        var childDrifts = new List<ObjectDriftResult>();

        // Columns
        var srcCols = src.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);
        var tgtCols = tgt.Columns.ToDictionary(c => c.ColumnName, StringComparer.OrdinalIgnoreCase);

        foreach (var (colName, srcCol) in srcCols)
        {
            if (!tgtCols.TryGetValue(colName, out var tgtCol))
            {
                var sev = SeverityEvaluator.EvaluateColumnDrift(DriftType.Added, srcCol, null);
                childDrifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.Column,
                    src.Schema,
                    srcCol.ColumnName,
                    DriftType.Added,
                    sev,
                    $"Column [{srcCol.ColumnName}] {srcCol.FormattedDataType} {(srcCol.IsNullable ? "NULL" : "NOT NULL")} is missing in target.",
                    SourceDefinition: FormatColumnDefinition(srcCol),
                    TargetDefinition: null
                ));
            }
            else
            {
                // Check if column differs
                if (AreColumnsDifferent(srcCol, tgtCol, out var diffDesc))
                {
                    var sev = SeverityEvaluator.EvaluateColumnDrift(DriftType.Modified, srcCol, tgtCol);
                    childDrifts.Add(new ObjectDriftResult(
                        DatabaseObjectType.Column,
                        src.Schema,
                        srcCol.ColumnName,
                        DriftType.Modified,
                        sev,
                        $"Column [{srcCol.ColumnName}] modified: {diffDesc}",
                        SourceDefinition: FormatColumnDefinition(srcCol),
                        TargetDefinition: FormatColumnDefinition(tgtCol)
                    ));
                }
            }
        }

        foreach (var (colName, tgtCol) in tgtCols)
        {
            if (!srcCols.ContainsKey(colName))
            {
                var sev = SeverityEvaluator.EvaluateColumnDrift(DriftType.Missing, null, tgtCol);
                childDrifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.Column,
                    tgt.Schema,
                    tgtCol.ColumnName,
                    DriftType.Missing,
                    sev,
                    $"Column [{tgtCol.ColumnName}] exists in target but not in source (DROP COLUMN candidate).",
                    SourceDefinition: null,
                    TargetDefinition: FormatColumnDefinition(tgtCol)
                ));
            }
        }

        // Indexes
        var srcIdxs = src.Indexes.ToDictionary(i => i.IndexName, StringComparer.OrdinalIgnoreCase);
        var tgtIdxs = tgt.Indexes.ToDictionary(i => i.IndexName, StringComparer.OrdinalIgnoreCase);

        foreach (var (idxName, srcIdx) in srcIdxs)
        {
            if (!tgtIdxs.TryGetValue(idxName, out var tgtIdx))
            {
                var objType = srcIdx.IsPrimaryKey ? DatabaseObjectType.PrimaryKey : DatabaseObjectType.Index;
                var sev = SeverityEvaluator.EvaluateIndexDrift(DriftType.Added, srcIdx);
                childDrifts.Add(new ObjectDriftResult(
                    objType,
                    src.Schema,
                    srcIdx.IndexName,
                    DriftType.Added,
                    sev,
                    $"{objType} [{srcIdx.IndexName}] missing in target.",
                    SourceDefinition: FormatIndexDefinition(src, srcIdx),
                    TargetDefinition: null
                ));
            }
            else
            {
                if (AreIndexesDifferent(srcIdx, tgtIdx, out var diffDesc))
                {
                    var objType = srcIdx.IsPrimaryKey ? DatabaseObjectType.PrimaryKey : DatabaseObjectType.Index;
                    var sev = SeverityEvaluator.EvaluateIndexDrift(DriftType.Modified, srcIdx);
                    childDrifts.Add(new ObjectDriftResult(
                        objType,
                        src.Schema,
                        srcIdx.IndexName,
                        DriftType.Modified,
                        sev,
                        $"{objType} [{srcIdx.IndexName}] modified: {diffDesc}",
                        SourceDefinition: FormatIndexDefinition(src, srcIdx),
                        TargetDefinition: FormatIndexDefinition(tgt, tgtIdx)
                    ));
                }
            }
        }

        foreach (var (idxName, tgtIdx) in tgtIdxs)
        {
            if (!srcIdxs.ContainsKey(idxName))
            {
                var objType = tgtIdx.IsPrimaryKey ? DatabaseObjectType.PrimaryKey : DatabaseObjectType.Index;
                var sev = SeverityEvaluator.EvaluateIndexDrift(DriftType.Missing, tgtIdx);
                childDrifts.Add(new ObjectDriftResult(
                    objType,
                    tgt.Schema,
                    tgtIdx.IndexName,
                    DriftType.Missing,
                    sev,
                    $"{objType} [{tgtIdx.IndexName}] present in target but not in source.",
                    SourceDefinition: null,
                    TargetDefinition: FormatIndexDefinition(tgt, tgtIdx)
                ));
            }
        }

        // Foreign Keys
        var srcFks = src.ForeignKeys.ToDictionary(fk => fk.ConstraintName, StringComparer.OrdinalIgnoreCase);
        var tgtFks = tgt.ForeignKeys.ToDictionary(fk => fk.ConstraintName, StringComparer.OrdinalIgnoreCase);

        foreach (var (fkName, srcFk) in srcFks)
        {
            if (!tgtFks.TryGetValue(fkName, out var tgtFk))
            {
                var sev = SeverityEvaluator.EvaluateForeignKeyDrift(DriftType.Added);
                childDrifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.ForeignKey,
                    src.Schema,
                    srcFk.ConstraintName,
                    DriftType.Added,
                    sev,
                    $"Foreign Key [{srcFk.ConstraintName}] missing in target.",
                    SourceDefinition: FormatForeignKeyDefinition(srcFk),
                    TargetDefinition: null
                ));
            }
            else
            {
                if (AreForeignKeysDifferent(srcFk, tgtFk, out var diffDesc))
                {
                    var sev = SeverityEvaluator.EvaluateForeignKeyDrift(DriftType.Modified);
                    childDrifts.Add(new ObjectDriftResult(
                        DatabaseObjectType.ForeignKey,
                        src.Schema,
                        srcFk.ConstraintName,
                        DriftType.Modified,
                        sev,
                        $"Foreign Key [{srcFk.ConstraintName}] modified: {diffDesc}",
                        SourceDefinition: FormatForeignKeyDefinition(srcFk),
                        TargetDefinition: FormatForeignKeyDefinition(tgtFk)
                    ));
                }
            }
        }

        foreach (var (fkName, tgtFk) in tgtFks)
        {
            if (!srcFks.ContainsKey(fkName))
            {
                var sev = SeverityEvaluator.EvaluateForeignKeyDrift(DriftType.Missing);
                childDrifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.ForeignKey,
                    tgt.Schema,
                    tgtFk.ConstraintName,
                    DriftType.Missing,
                    sev,
                    $"Foreign Key [{tgtFk.ConstraintName}] exists in target but not in source.",
                    SourceDefinition: null,
                    TargetDefinition: FormatForeignKeyDefinition(tgtFk)
                ));
            }
        }

        // Check Constraints
        var srcCks = src.CheckConstraints.ToDictionary(c => c.ConstraintName, StringComparer.OrdinalIgnoreCase);
        var tgtCks = tgt.CheckConstraints.ToDictionary(c => c.ConstraintName, StringComparer.OrdinalIgnoreCase);

        foreach (var (ckName, srcCk) in srcCks)
        {
            if (!tgtCks.TryGetValue(ckName, out var tgtCk))
            {
                childDrifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.CheckConstraint,
                    src.Schema,
                    srcCk.ConstraintName,
                    DriftType.Added,
                    DriftSeverity.Low,
                    $"Check Constraint [{srcCk.ConstraintName}] missing in target.",
                    SourceDefinition: $"ALTER TABLE [{src.Schema}].[{src.TableName}] ADD CONSTRAINT [{srcCk.ConstraintName}] CHECK {srcCk.Definition};",
                    TargetDefinition: null
                ));
            }
            else
            {
                var srcDef = CleanDefaultValue(srcCk.Definition);
                var tgtDef = CleanDefaultValue(tgtCk.Definition);
                if (!string.Equals(srcDef, tgtDef, StringComparison.OrdinalIgnoreCase))
                {
                    childDrifts.Add(new ObjectDriftResult(
                        DatabaseObjectType.CheckConstraint,
                        src.Schema,
                        srcCk.ConstraintName,
                        DriftType.Modified,
                        DriftSeverity.Medium,
                        $"Check Constraint [{srcCk.ConstraintName}] modified: '{tgtCk.Definition}' -> '{srcCk.Definition}'",
                        SourceDefinition: $"ALTER TABLE [{src.Schema}].[{src.TableName}] ADD CONSTRAINT [{srcCk.ConstraintName}] CHECK {srcCk.Definition};",
                        TargetDefinition: $"ALTER TABLE [{tgt.Schema}].[{tgt.TableName}] ADD CONSTRAINT [{tgtCk.ConstraintName}] CHECK {tgtCk.Definition};"
                    ));
                }
            }
        }

        foreach (var (ckName, tgtCk) in tgtCks)
        {
            if (!srcCks.ContainsKey(ckName))
            {
                childDrifts.Add(new ObjectDriftResult(
                    DatabaseObjectType.CheckConstraint,
                    tgt.Schema,
                    tgtCk.ConstraintName,
                    DriftType.Missing,
                    DriftSeverity.Breaking,
                    $"Check Constraint [{tgtCk.ConstraintName}] exists in target but not in source.",
                    SourceDefinition: null,
                    TargetDefinition: $"ALTER TABLE [{tgt.Schema}].[{tgt.TableName}] DROP CONSTRAINT [{tgtCk.ConstraintName}];"
                ));
            }
        }

        return childDrifts;
    }

    private static string CleanDefaultValue(string? def)
    {
        if (string.IsNullOrWhiteSpace(def))
            return string.Empty;
        var trimmed = def.Trim();
        while (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[1..^1].Trim();
        }
        return trimmed;
    }

    private bool AreColumnsDifferent(ColumnModel src, ColumnModel tgt, out string diff)
    {
        var diffs = new List<string>();
        var dt = src.DataType.ToLowerInvariant();
        var isCharOrBinary = dt is "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary";
        var isDecimal = dt is "decimal" or "numeric";
        var isTemporalWithScale = dt is "datetime2" or "datetimeoffset" or "time";

        if (!src.DataType.Equals(tgt.DataType, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"Type: {tgt.DataType} -> {src.DataType}");

        if (isCharOrBinary && src.MaxLength != tgt.MaxLength)
            diffs.Add($"MaxLength: {tgt.MaxLength} -> {src.MaxLength}");

        if (isDecimal && (src.Precision != tgt.Precision || src.Scale != tgt.Scale))
            diffs.Add($"Precision/Scale: ({tgt.Precision},{tgt.Scale}) -> ({src.Precision},{src.Scale})");

        if (isTemporalWithScale && src.Scale != tgt.Scale)
            diffs.Add($"Scale: {tgt.Scale} -> {src.Scale}");

        if (src.IsNullable != tgt.IsNullable)
            diffs.Add($"Nullable: {tgt.IsNullable} -> {src.IsNullable}");

        if (src.IsIdentity != tgt.IsIdentity)
            diffs.Add($"Identity: {tgt.IsIdentity} -> {src.IsIdentity}");

        var srcDefClean = CleanDefaultValue(src.DefaultValue);
        var tgtDefClean = CleanDefaultValue(tgt.DefaultValue);
        if (!string.Equals(srcDefClean, tgtDefClean, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"Default: '{tgt.DefaultValue}' -> '{src.DefaultValue}'");

        if (diffs.Count > 0)
        {
            diff = string.Join("; ", diffs);
            return true;
        }

        diff = string.Empty;
        return false;
    }

    private bool AreIndexesDifferent(IndexModel src, IndexModel tgt, out string diff)
    {
        var diffs = new List<string>();

        if (src.IsUnique != tgt.IsUnique)
            diffs.Add($"Unique: {tgt.IsUnique} -> {src.IsUnique}");

        if (src.IsPrimaryKey != tgt.IsPrimaryKey)
            diffs.Add($"PrimaryKey: {tgt.IsPrimaryKey} -> {src.IsPrimaryKey}");

        if (src.IsClustered != tgt.IsClustered)
            diffs.Add($"Clustered: {tgt.IsClustered} -> {src.IsClustered}");

        if (!src.KeyColumns.SequenceEqual(tgt.KeyColumns, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"KeyColumns: [{string.Join(", ", tgt.KeyColumns)}] -> [{string.Join(", ", src.KeyColumns)}]");

        if (!src.IncludedColumns.SequenceEqual(tgt.IncludedColumns, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"IncludedColumns: [{string.Join(", ", tgt.IncludedColumns)}] -> [{string.Join(", ", src.IncludedColumns)}]");

        if (diffs.Count > 0)
        {
            diff = string.Join("; ", diffs);
            return true;
        }

        diff = string.Empty;
        return false;
    }

    private bool AreForeignKeysDifferent(ForeignKeyModel src, ForeignKeyModel tgt, out string diff)
    {
        var diffs = new List<string>();

        if (!src.ReferencedSchema.Equals(tgt.ReferencedSchema, StringComparison.OrdinalIgnoreCase) ||
            !src.ReferencedTable.Equals(tgt.ReferencedTable, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add($"ReferencedTable: [{tgt.ReferencedSchema}].[{tgt.ReferencedTable}] -> [{src.ReferencedSchema}].[{src.ReferencedTable}]");
        }

        if (!src.ParentColumns.SequenceEqual(tgt.ParentColumns, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"ParentColumns: [{string.Join(", ", tgt.ParentColumns)}] -> [{string.Join(", ", src.ParentColumns)}]");

        if (!src.ReferencedColumns.SequenceEqual(tgt.ReferencedColumns, StringComparer.OrdinalIgnoreCase))
            diffs.Add($"ReferencedColumns: [{string.Join(", ", tgt.ReferencedColumns)}] -> [{string.Join(", ", src.ReferencedColumns)}]");

        if (!src.OnDeleteAction.Equals(tgt.OnDeleteAction, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"OnDelete: {tgt.OnDeleteAction} -> {src.OnDeleteAction}");

        if (!src.OnUpdateAction.Equals(tgt.OnUpdateAction, StringComparison.OrdinalIgnoreCase))
            diffs.Add($"OnUpdate: {tgt.OnUpdateAction} -> {src.OnUpdateAction}");

        if (diffs.Count > 0)
        {
            diff = string.Join("; ", diffs);
            return true;
        }

        diff = string.Empty;
        return false;
    }

    private static string FormatColumnDefinition(ColumnModel c)
    {
        var identity = c.IsIdentity ? " IDENTITY(1,1)" : "";
        var nullability = c.IsNullable ? "NULL" : "NOT NULL";
        var def = !string.IsNullOrEmpty(c.DefaultValue) ? $" DEFAULT {c.DefaultValue}" : "";
        return $"[{c.ColumnName}] {c.FormattedDataType}{identity} {nullability}{def}";
    }

    private static string FormatIndexDefinition(TableModel table, IndexModel idx)
    {
        var unique = idx.IsUnique ? "UNIQUE " : "";
        var clustered = idx.IsClustered ? "CLUSTERED" : "NONCLUSTERED";
        var keys = string.Join(", ", idx.KeyColumns.Select(k => $"[{k}]"));
        var incs = idx.IncludedColumns.Count > 0 ? $" INCLUDE ({string.Join(", ", idx.IncludedColumns.Select(i => $"[{i}]"))})" : "";
        var filter = !string.IsNullOrEmpty(idx.FilterDefinition) ? $" WHERE {idx.FilterDefinition}" : "";
        return $"CREATE {unique}{clustered} INDEX [{idx.IndexName}] ON [{table.Schema}].[{table.TableName}] ({keys}){incs}{filter};";
    }

    private static string FormatForeignKeyDefinition(ForeignKeyModel fk)
    {
        var parentCols = string.Join(", ", fk.ParentColumns.Select(c => $"[{c}]"));
        var refCols = string.Join(", ", fk.ReferencedColumns.Select(c => $"[{c}]"));
        return $"ALTER TABLE [{fk.ParentSchema}].[{fk.ParentTable}] ADD CONSTRAINT [{fk.ConstraintName}] FOREIGN KEY ({parentCols}) REFERENCES [{fk.ReferencedSchema}].[{fk.ReferencedTable}] ({refCols}) ON DELETE {fk.OnDeleteAction.Replace('_', ' ')} ON UPDATE {fk.OnUpdateAction.Replace('_', ' ')};";
    }

    private static string FormatTableDefinition(TableModel table)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CREATE TABLE [{table.Schema}].[{table.TableName}] (");
        for (int i = 0; i < table.Columns.Count; i++)
        {
            var col = table.Columns[i];
            var comma = i < table.Columns.Count - 1 ? "," : "";
            sb.AppendLine($"    {FormatColumnDefinition(col)}{comma}");
        }
        sb.AppendLine(");");
        return sb.ToString();
    }
}
