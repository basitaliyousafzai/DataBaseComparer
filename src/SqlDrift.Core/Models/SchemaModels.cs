using System.Text.Json.Serialization;

namespace SqlDrift.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseObjectType
{
    Table,
    Data,
    Column,
    Index,
    ForeignKey,
    PrimaryKey,
    CheckConstraint,
    View,
    StoredProcedure,
    UserDefinedFunction,
    Trigger
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftType
{
    Unchanged,
    Added,       // Exists in Source, missing in Target (needs to be created in Target)
    Missing,     // Missing in Source, exists in Target (needs to be dropped in Target to match Source)
    Modified     // Exists in both but structural or definition differences exist
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriftSeverity
{
    None = 0,
    Low = 1,       // Safe additions (new nullable column, new SP, new index)
    Medium = 2,    // Modified SP/View, widening column data type
    Breaking = 3   // Dropping column/table, narrowing data type, NULL -> NOT NULL
}

public record ColumnModel(
    string ColumnName,
    string DataType,
    int MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    bool IsIdentity,
    string? DefaultValue = null,
    string? ComputedDefinition = null,
    string? Collation = null
)
{
    public string FormattedDataType
    {
        get
        {
            var dt = DataType.ToLowerInvariant();
            if (dt is "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary")
            {
                var lenStr = MaxLength == -1 ? "MAX" : (dt.StartsWith("n") ? (MaxLength / 2).ToString() : MaxLength.ToString());
                return $"{DataType.ToUpperInvariant()}({lenStr})";
            }
            if (dt is "decimal" or "numeric")
            {
                return $"{DataType.ToUpperInvariant()}({Precision},{Scale})";
            }
            if (dt is "datetime2" or "datetimeoffset" or "time")
            {
                return $"{DataType.ToUpperInvariant()}({Scale})";
            }
            return DataType.ToUpperInvariant();
        }
    }
}

public record IndexModel(
    string IndexName,
    bool IsUnique,
    bool IsPrimaryKey,
    bool IsClustered,
    string? FilterDefinition,
    List<string> KeyColumns,
    List<string> IncludedColumns
);

public record ForeignKeyModel(
    string ConstraintName,
    string ParentSchema,
    string ParentTable,
    List<string> ParentColumns,
    string ReferencedSchema,
    string ReferencedTable,
    List<string> ReferencedColumns,
    string OnDeleteAction = "NO_ACTION",
    string OnUpdateAction = "NO_ACTION"
);

public record CheckConstraintModel(
    string ConstraintName,
    string Definition,
    bool IsDisabled = false
);

public record TableModel(
    string Schema,
    string TableName,
    List<ColumnModel> Columns,
    List<IndexModel> Indexes,
    List<ForeignKeyModel> ForeignKeys,
    List<CheckConstraintModel>? CheckConstraints = null
)
{
    public string FullName => $"[{Schema}].[{TableName}]";
    public List<CheckConstraintModel> CheckConstraints { get; init; } = CheckConstraints ?? [];
}

public record ProgrammableObjectModel(
    DatabaseObjectType ObjectType,
    string Schema,
    string ObjectName,
    string DefinitionScript,
    string NormalizedHash
)
{
    public string FullName => $"[{Schema}].[{ObjectName}]";
}

public record DatabaseSchemaSnapshot(
    string DatabaseName,
    DateTime ExtractedAtUtc,
    Dictionary<string, TableModel> Tables,
    Dictionary<string, ProgrammableObjectModel> ProgrammableObjects,
    Dictionary<string, string>? TableDataHashes = null,
    Dictionary<string, long>? TableRowCounts = null
)
{
    public Dictionary<string, string> TableDataHashes { get; init; } = TableDataHashes ?? new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, long> TableRowCounts { get; init; } = TableRowCounts ?? new(StringComparer.OrdinalIgnoreCase);

    public static DatabaseSchemaSnapshot CreateEmpty(string databaseName = "Empty") =>
        new(databaseName, DateTime.UtcNow, new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase));
}

public record ObjectDriftResult(
    DatabaseObjectType ObjectType,
    string Schema,
    string Name,
    DriftType DriftType,
    DriftSeverity Severity,
    string Description,
    string? SourceDefinition = null,
    string? TargetDefinition = null,
    List<ObjectDriftResult>? Children = null
)
{
    public string FullName => string.IsNullOrWhiteSpace(Schema) ? $"[{Name}]" : $"[{Schema}].[{Name}]";
}

public record SchemaDriftReport(
    string SourceIdentifier,
    string TargetIdentifier,
    DateTime AnalyzedAtUtc,
    List<ObjectDriftResult> Drifts
)
{
    public int TotalDrifts => Drifts.Count;
    public int BreakingCount => Drifts.Count(d => d.Severity == DriftSeverity.Breaking) 
                               + Drifts.Sum(d => d.Children?.Count(c => c.Severity == DriftSeverity.Breaking) ?? 0);
    public int MediumCount => Drifts.Count(d => d.Severity == DriftSeverity.Medium)
                             + Drifts.Sum(d => d.Children?.Count(c => c.Severity == DriftSeverity.Medium) ?? 0);
    public int LowCount => Drifts.Count(d => d.Severity == DriftSeverity.Low)
                          + Drifts.Sum(d => d.Children?.Count(c => c.Severity == DriftSeverity.Low) ?? 0);

    public int AddedCount => Drifts.Count(d => d.DriftType == DriftType.Added);
    public int MissingCount => Drifts.Count(d => d.DriftType == DriftType.Missing);
    public int ModifiedCount => Drifts.Count(d => d.DriftType == DriftType.Modified);

    public bool HasDrift => Drifts.Any(d => d.DriftType != DriftType.Unchanged);
    public bool HasBreakingDrift => BreakingCount > 0;
}
