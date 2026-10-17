using System.Data;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlDrift.Core.Models;

namespace SqlDrift.Core.Extractor;

public class LiveCatalogExtractorOptions
{
    public bool StrictCollation { get; set; } = false;
    public string DefaultSchema { get; set; } = "dbo";
}

public class LiveCatalogExtractor
{
    private readonly LiveCatalogExtractorOptions _options;

    public LiveCatalogExtractor(LiveCatalogExtractorOptions? options = null)
    {
        _options = options ?? new LiveCatalogExtractorOptions();
    }

    public async Task<DatabaseSchemaSnapshot> ExtractAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var databaseName = connection.Database;
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            databaseName = "Master";
        }

        var tables = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);
        var programmableObjects = new Dictionary<string, ProgrammableObjectModel>(StringComparer.OrdinalIgnoreCase);
        var tableDataHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tableRowCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // 1. Query Tables
        const string tablesQuery = """
            SELECT s.name AS [Schema], t.name AS [TableName]
            FROM sys.tables t
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name;
            """;

        await using (var cmd = new SqlCommand(tablesQuery, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var tableName = reader.GetString(1);
                var key = $"{schema}.{tableName}";
                tables[key] = new TableModel(schema, tableName, [], [], []);
            }
        }

        // 2. Query Columns
        const string columnsQuery = """
            SELECT 
                s.name AS [Schema],
                t.name AS [TableName],
                c.name AS [ColumnName],
                ty.name AS [DataType],
                c.max_length AS [MaxLength],
                c.precision AS [Precision],
                c.scale AS [Scale],
                c.is_nullable AS [IsNullable],
                c.is_identity AS [IsIdentity],
                d.definition AS [DefaultValue],
                cc.definition AS [ComputedDefinition],
                c.collation_name AS [Collation]
            FROM sys.columns c
            INNER JOIN sys.tables t ON c.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.default_constraints d ON c.default_object_id = d.object_id
            LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, c.column_id;
            """;

        await using (var cmd = new SqlCommand(columnsQuery, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var tableName = reader.GetString(1);
                var key = $"{schema}.{tableName}";

                if (!tables.TryGetValue(key, out var table))
                    continue;

                var colName = reader.GetString(2);
                var dataType = reader.GetString(3);
                var maxLen = Convert.ToInt32(reader.GetInt16(4));
                var precision = reader.GetByte(5);
                var scale = reader.GetByte(6);
                var isNullable = reader.GetBoolean(7);
                var isIdentity = reader.GetBoolean(8);
                var defVal = reader.IsDBNull(9) ? null : CleanDefaultValue(reader.GetString(9));
                var compDef = reader.IsDBNull(10) ? null : reader.GetString(10);
                var collation = reader.IsDBNull(11) ? null : reader.GetString(11);

                if (!_options.StrictCollation)
                {
                    collation = null;
                }

                table.Columns.Add(new ColumnModel(
                    colName,
                    dataType,
                    maxLen,
                    precision,
                    scale,
                    isNullable,
                    isIdentity,
                    defVal,
                    compDef,
                    collation
                ));
            }
        }

        // 3. Query Indexes & Primary Keys
        const string indexesQuery = """
            SELECT 
                s.name AS [Schema],
                t.name AS [TableName],
                i.name AS [IndexName],
                i.is_unique AS [IsUnique],
                i.is_primary_key AS [IsPrimaryKey],
                i.type_desc AS [IndexType],
                i.filter_definition AS [FilterDefinition],
                c.name AS [ColumnName],
                ic.is_included_column AS [IsIncluded],
                ic.key_ordinal AS [KeyOrdinal]
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE t.is_ms_shipped = 0 AND i.name IS NOT NULL
            ORDER BY s.name, t.name, i.name, ic.key_ordinal;
            """;

        var indexMap = new Dictionary<string, (IndexModel Index, List<string> Keys, List<string> Incs)>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = new SqlCommand(indexesQuery, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var tableName = reader.GetString(1);
                var tableKey = $"{schema}.{tableName}";
                var indexName = reader.GetString(2);
                var isUnique = reader.GetBoolean(3);
                var isPk = reader.GetBoolean(4);
                var indexType = reader.GetString(5);
                var filter = reader.IsDBNull(6) ? null : reader.GetString(6);
                var colName = reader.GetString(7);
                var isIncluded = reader.GetBoolean(8);

                var isClustered = indexType.Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase);
                var indexKey = $"{tableKey}::{indexName}";

                if (!indexMap.TryGetValue(indexKey, out var entry))
                {
                    var keys = new List<string>();
                    var incs = new List<string>();
                    var index = new IndexModel(indexName, isUnique, isPk, isClustered, filter, keys, incs);
                    entry = (index, keys, incs);
                    indexMap[indexKey] = entry;

                    if (tables.TryGetValue(tableKey, out var table))
                    {
                        table.Indexes.Add(index);
                    }
                }

                if (isIncluded)
                {
                    entry.Incs.Add(colName);
                }
                else
                {
                    entry.Keys.Add(colName);
                }
            }
        }

        // 4. Query Foreign Keys
        const string fkQuery = """
            SELECT 
                fk.name AS [ConstraintName],
                ps.name AS [ParentSchema],
                pt.name AS [ParentTable],
                pc.name AS [ParentColumn],
                rs.name AS [ReferencedSchema],
                rt.name AS [ReferencedTable],
                rc.name AS [ReferencedColumn],
                fk.delete_referential_action_desc AS [OnDeleteAction],
                fk.update_referential_action_desc AS [OnUpdateAction],
                fkc.constraint_column_id AS [Ordinal]
            FROM sys.foreign_keys fk
            INNER JOIN sys.tables pt ON fk.parent_object_id = pt.object_id
            INNER JOIN sys.schemas ps ON pt.schema_id = ps.schema_id
            INNER JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
            INNER JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
            INNER JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            INNER JOIN sys.columns pc ON fkc.parent_object_id = pc.object_id AND fkc.parent_column_id = pc.column_id
            INNER JOIN sys.columns rc ON fkc.referenced_object_id = rc.object_id AND fkc.referenced_column_id = rc.column_id
            WHERE pt.is_ms_shipped = 0
            ORDER BY fk.name, fkc.constraint_column_id;
            """;

        var fkMap = new Dictionary<string, (ForeignKeyModel Model, List<string> ParentCols, List<string> RefCols)>(StringComparer.OrdinalIgnoreCase);

        await using (var cmd = new SqlCommand(fkQuery, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var fkName = reader.GetString(0);
                var pSchema = reader.GetString(1);
                var pTable = reader.GetString(2);
                var pCol = reader.GetString(3);
                var rSchema = reader.GetString(4);
                var rTable = reader.GetString(5);
                var rCol = reader.GetString(6);
                var onDelete = reader.GetString(7);
                var onUpdate = reader.GetString(8);

                var tableKey = $"{pSchema}.{pTable}";
                var fkKey = $"{tableKey}::{fkName}";

                if (!fkMap.TryGetValue(fkKey, out var entry))
                {
                    var parentCols = new List<string>();
                    var refCols = new List<string>();
                    var fk = new ForeignKeyModel(
                        fkName,
                        pSchema,
                        pTable,
                        parentCols,
                        rSchema,
                        rTable,
                        refCols,
                        onDelete,
                        onUpdate
                    );
                    entry = (fk, parentCols, refCols);
                    fkMap[fkKey] = entry;

                    if (tables.TryGetValue(tableKey, out var table))
                    {
                        table.ForeignKeys.Add(fk);
                    }
                }

                entry.ParentCols.Add(pCol);
                entry.RefCols.Add(rCol);
            }
        }

        // 5. Query Check Constraints
        const string checkQuery = """
            SELECT 
                s.name AS [Schema],
                t.name AS [TableName],
                cc.name AS [ConstraintName],
                cc.definition AS [Definition],
                cc.is_disabled AS [IsDisabled]
            FROM sys.check_constraints cc
            INNER JOIN sys.tables t ON cc.parent_object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            WHERE t.is_ms_shipped = 0
            ORDER BY s.name, t.name, cc.name;
            """;

        await using (var cmd = new SqlCommand(checkQuery, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var tableName = reader.GetString(1);
                var constName = reader.GetString(2);
                var def = reader.GetString(3);
                var isDisabled = reader.GetBoolean(4);

                var tableKey = $"{schema}.{tableName}";
                if (tables.TryGetValue(tableKey, out var table))
                {
                    table.CheckConstraints.Add(new CheckConstraintModel(constName, def, isDisabled));
                }
            }
        }

        // 6. Query Programmable Objects (SP, View, Function, Trigger)
        const string progQuery = """
            SELECT 
                s.name AS [Schema],
                o.name AS [ObjectName],
                o.type AS [RawType],
                m.definition AS [DefinitionScript]
            FROM sys.objects o
            INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
            INNER JOIN sys.sql_modules m ON o.object_id = m.object_id
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('P', 'V', 'FN', 'IF', 'TF', 'TR')
            ORDER BY s.name, o.name;
            """;

        await using (var cmd = new SqlCommand(progQuery, connection))
        await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var objName = reader.GetString(1);
                var rawType = reader.GetString(2).Trim();
                var script = reader.IsDBNull(3) ? "" : reader.GetString(3);

                var objType = rawType switch
                {
                    "P" => DatabaseObjectType.StoredProcedure,
                    "V" => DatabaseObjectType.View,
                    "FN" or "IF" or "TF" => DatabaseObjectType.UserDefinedFunction,
                    "TR" => DatabaseObjectType.Trigger,
                    _ => DatabaseObjectType.StoredProcedure
                };

                var hash = AstNormalizer.ComputeNormalizedHash(script);
                var key = $"{schema}.{objName}";

                programmableObjects[key] = new ProgrammableObjectModel(
                    objType,
                    schema,
                    objName,
                    script,
                    hash
                );
            }
        }

        // Compute row-level data hashes for all tables
        foreach (var (tableKey, table) in tables)
        {
            try
            {
                var (count, hash) = await ComputeTableDataHashAsync(connection, table.Schema, table.TableName, cancellationToken);
                tableRowCounts[tableKey] = count;
                tableDataHashes[tableKey] = hash;
            }
            catch
            {
                // Skip tables that cannot be read (permissions, etc.)
            }
        }

        return new DatabaseSchemaSnapshot(databaseName, DateTime.UtcNow, tables, programmableObjects, tableDataHashes, tableRowCounts);
    }

    private static string CleanDefaultValue(string def)
    {
        var trimmed = def.Trim();
        // Remove outer parens if wrapped like ((0)) or ('val')
        while (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
        {
            trimmed = trimmed[1..^1].Trim();
        }
        return trimmed;
    }

    private static async Task<(long Count, string Hash)> ComputeTableDataHashAsync(
        SqlConnection connection,
        string schema,
        string tableName,
        CancellationToken cancellationToken)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        long count = 0;
        var sql = $"SELECT * FROM [{schema}].[{tableName}]";
        await using var cmd = new SqlCommand(sql, connection);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        var fieldCount = reader.FieldCount;
        while (await reader.ReadAsync(cancellationToken))
        {
            count++;
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < fieldCount; i++)
            {
                var value = reader.IsDBNull(i) ? "<NULL>" : reader.GetValue(i)?.ToString() ?? string.Empty;
                sb.Append(i).Append('=').Append(value).Append('|');
            }
            sb.Append('\n');
            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return (count, Convert.ToHexString(sha.Hash!));
    }
}
