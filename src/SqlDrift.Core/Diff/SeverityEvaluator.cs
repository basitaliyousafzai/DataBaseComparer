using SqlDrift.Core.Models;

namespace SqlDrift.Core.Diff;

public static class SeverityEvaluator
{
    public static DriftSeverity EvaluateTableDrift(DriftType driftType) => driftType switch
    {
        DriftType.Added => DriftSeverity.Low,
        DriftType.Missing => DriftSeverity.Breaking, // Dropping a table is breaking
        DriftType.Modified => DriftSeverity.Medium,
        _ => DriftSeverity.None
    };

    public static DriftSeverity EvaluateColumnDrift(DriftType driftType, ColumnModel? source, ColumnModel? target)
    {
        if (driftType == DriftType.Added)
        {
            // Adding a NOT NULL column without a default value is Breaking because existing rows cannot be populated
            if (source != null && !source.IsNullable && string.IsNullOrEmpty(source.DefaultValue) && !source.IsIdentity)
            {
                return DriftSeverity.Breaking;
            }
            return DriftSeverity.Low;
        }

        if (driftType == DriftType.Missing)
        {
            // Dropping a column is Breaking (data loss)
            return DriftSeverity.Breaking;
        }

        if (driftType == DriftType.Modified && source != null && target != null)
        {
            // Altering nullability from NULL to NOT NULL is Breaking
            if (!source.IsNullable && target.IsNullable)
            {
                return DriftSeverity.Breaking;
            }

            // Narrowing data type length or type is Breaking
            if (IsTypeNarrowing(source, target))
            {
                return DriftSeverity.Breaking;
            }

            return DriftSeverity.Medium;
        }

        return DriftSeverity.None;
    }

    public static DriftSeverity EvaluateIndexDrift(DriftType driftType, IndexModel? model)
    {
        if (driftType == DriftType.Added)
            return DriftSeverity.Low;

        if (driftType == DriftType.Missing)
        {
            // Dropping a primary key is breaking
            if (model?.IsPrimaryKey == true)
                return DriftSeverity.Breaking;
            return DriftSeverity.Medium;
        }

        return DriftSeverity.Medium;
    }

    public static DriftSeverity EvaluateForeignKeyDrift(DriftType driftType)
    {
        if (driftType == DriftType.Added)
            return DriftSeverity.Low;

        // Dropping foreign key constraint
        if (driftType == DriftType.Missing)
            return DriftSeverity.Breaking;

        return DriftSeverity.Medium;
    }

    public static DriftSeverity EvaluateProgrammableObjectDrift(DriftType driftType, DatabaseObjectType objectType)
    {
        return driftType switch
        {
            DriftType.Added => DriftSeverity.Low,
            DriftType.Modified => DriftSeverity.Medium,
            DriftType.Missing => DriftSeverity.Breaking, // Dropping a SP/View/Function/Trigger is breaking
            _ => DriftSeverity.None
        };
    }

    public static bool IsTypeNarrowing(ColumnModel source, ColumnModel target)
    {
        var srcType = source.DataType.ToLowerInvariant();
        var tgtType = target.DataType.ToLowerInvariant();

        // If data type changed completely
        if (!srcType.Equals(tgtType, StringComparison.OrdinalIgnoreCase))
        {
            // Explicit safe widening conversions (NOT narrowing):
            if (tgtType is "tinyint" && srcType is "smallint" or "int" or "bigint") return false;
            if (tgtType is "smallint" && srcType is "int" or "bigint") return false;
            if (tgtType is "int" && srcType is "bigint") return false;
            if (tgtType is "float" && srcType is "float") return false;
            if (tgtType is "real" && srcType is "float") return false;
            if (tgtType is "varchar" && srcType is "nvarchar")
            {
                if (source.MaxLength == -1 || (target.MaxLength != -1 && source.MaxLength >= target.MaxLength))
                    return false;
            }

            // Explicit narrowing conversions:
            if (tgtType == "bigint" && srcType is "int" or "smallint" or "tinyint") return true;
            if (tgtType == "int" && srcType is "smallint" or "tinyint") return true;
            if (tgtType == "smallint" && srcType is "tinyint") return true;
            if (tgtType is "nvarchar" && srcType is "varchar") return true;

            return true; // Any other incompatible type conversion is considered potentially breaking narrowing
        }

        // Same type, check string/binary length narrowing:
        // Note: -1 means MAX
        if (srcType is "varchar" or "nvarchar" or "char" or "nchar" or "binary" or "varbinary")
        {
            if (target.MaxLength == -1 && source.MaxLength != -1)
                return true; // MAX changed to a fixed length

            if (source.MaxLength != -1 && target.MaxLength != -1 && source.MaxLength < target.MaxLength)
                return true; // Target was longer than Source
        }

        // Numeric precision / scale narrowing
        if (srcType is "decimal" or "numeric")
        {
            if (source.Precision < target.Precision || source.Scale < target.Scale)
                return true;
        }

        return false;
    }
}
