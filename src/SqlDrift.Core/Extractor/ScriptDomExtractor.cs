using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlDrift.Core.Models;

namespace SqlDrift.Core.Extractor;

public class ScriptDomExtractor
{
    private readonly string _defaultSchema;

    public ScriptDomExtractor(string defaultSchema = "dbo")
    {
        _defaultSchema = defaultSchema;
    }

    public async Task<DatabaseSchemaSnapshot> ExtractFromDirectoryOrFileAsync(string path, string databaseName = "SqlScriptDb", CancellationToken cancellationToken = default)
    {
        var sqlFiles = new List<string>();

        if (Directory.Exists(path))
        {
            sqlFiles.AddRange(Directory.GetFiles(path, "*.sql", SearchOption.AllDirectories));
        }
        else if (File.Exists(path))
        {
            sqlFiles.Add(path);
        }
        else
        {
            throw new FileNotFoundException($"Path not found: {path}", path);
        }

        var fullSql = new System.Text.StringBuilder();
        foreach (var file in sqlFiles.OrderBy(f => f))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = await File.ReadAllTextAsync(file, cancellationToken);
            fullSql.AppendLine(content);
            fullSql.AppendLine("\nGO\n");
        }

        return ExtractFromScript(fullSql.ToString(), databaseName);
    }

    public DatabaseSchemaSnapshot ExtractFromScript(string script, string databaseName = "SqlScriptDb")
    {
        var tables = new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);
        var programmableObjects = new Dictionary<string, ProgrammableObjectModel>(StringComparer.OrdinalIgnoreCase);

        // Split by GO statements
        var batches = Regex.Split(script, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        var parser = new TSql160Parser(true);

        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
                continue;

            using var reader = new StringReader(batch);
            var fragment = parser.Parse(reader, out var errors);

            if (fragment == null)
                continue;

            var visitor = new SchemaFragmentVisitor(_defaultSchema, tables, programmableObjects, batch);
            fragment.Accept(visitor);
        }

        return new DatabaseSchemaSnapshot(databaseName, DateTime.UtcNow, tables, programmableObjects);
    }

    private sealed class SchemaFragmentVisitor : TSqlFragmentVisitor
    {
        private readonly string _defaultSchema;
        private readonly Dictionary<string, TableModel> _tables;
        private readonly Dictionary<string, ProgrammableObjectModel> _programmableObjects;
        private readonly string _rawBatch;

        public SchemaFragmentVisitor(
            string defaultSchema,
            Dictionary<string, TableModel> tables,
            Dictionary<string, ProgrammableObjectModel> programmableObjects,
            string rawBatch)
        {
            _defaultSchema = defaultSchema;
            _tables = tables;
            _programmableObjects = programmableObjects;
            _rawBatch = rawBatch;
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            var schema = node.SchemaObjectName.SchemaIdentifier?.Value ?? _defaultSchema;
            var tableName = node.SchemaObjectName.BaseIdentifier.Value;
            var key = $"{schema}.{tableName}";

            if (!_tables.TryGetValue(key, out var table))
            {
                table = new TableModel(schema, tableName, [], [], []);
                _tables[key] = table;
            }

            if (node.Definition != null)
            {
                foreach (var colDef in node.Definition.ColumnDefinitions)
                {
                    var colName = colDef.ColumnIdentifier.Value;
                    var dataTypeStr = "nvarchar";
                    int maxLen = 50;
                    byte precision = 18;
                    byte scale = 0;

                    if (colDef.DataType != null)
                    {
                        dataTypeStr = colDef.DataType.Name.BaseIdentifier.Value.ToLowerInvariant();
                        if (colDef.DataType is SqlDataTypeReference sqlDt)
                        {
                            dataTypeStr = sqlDt.SqlDataTypeOption.ToString().ToLowerInvariant();
                            maxLen = GetDefaultMaxLength(dataTypeStr);
                            precision = GetDefaultPrecision(dataTypeStr);
                            scale = GetDefaultScale(dataTypeStr);

                            if (sqlDt.Parameters.Count >= 1 && int.TryParse(sqlDt.Parameters[0].Value, out var p1))
                            {
                                maxLen = dataTypeStr.StartsWith("n") ? p1 * 2 : p1;
                                precision = (byte)p1;
                            }
                            else if (sqlDt.Parameters.Count >= 1 && sqlDt.Parameters[0].Value.Equals("max", StringComparison.OrdinalIgnoreCase))
                            {
                                maxLen = -1;
                            }

                            if (sqlDt.Parameters.Count >= 2 && byte.TryParse(sqlDt.Parameters[1].Value, out var p2))
                            {
                                scale = p2;
                            }
                        }
                    }

                    bool isNullable = true;
                    bool isIdentity = colDef.IdentityOptions != null;
                    string? defVal = null;

                    if (colDef.DefaultConstraint != null)
                    {
                        defVal = CleanDefaultValue(GetNodeText(colDef.DefaultConstraint.Expression));
                    }

                    foreach (var constraint in colDef.Constraints)
                    {
                        if (constraint is NullableConstraintDefinition nullDef)
                        {
                            isNullable = nullDef.Nullable;
                        }
                        else if (constraint is DefaultConstraintDefinition defDef)
                        {
                            defVal = CleanDefaultValue(GetNodeText(defDef.Expression));
                        }
                        else if (constraint is UniqueConstraintDefinition uniqueConstraint && uniqueConstraint.IsPrimaryKey)
                        {
                            isNullable = false;
                            var pkIndexName = uniqueConstraint.ConstraintIdentifier?.Value ?? $"PK_{tableName}";
                            var pkIndex = new IndexModel(
                                pkIndexName,
                                true,
                                true,
                                uniqueConstraint.Clustered ?? true,
                                null,
                                [colName],
                                []
                            );
                            table.Indexes.Add(pkIndex);
                        }
                    }

                    table.Columns.Add(new ColumnModel(
                        colName,
                        dataTypeStr,
                        maxLen,
                        precision,
                        scale,
                        isNullable,
                        isIdentity,
                        defVal
                    ));
                }

                // Table-level constraints
                foreach (var constraint in node.Definition.TableConstraints)
                {
                    ProcessTableConstraint(table, constraint);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateIndexStatement node)
        {
            var schema = node.OnName.SchemaIdentifier?.Value ?? _defaultSchema;
            var tableName = node.OnName.BaseIdentifier.Value;
            var tableKey = $"{schema}.{tableName}";

            if (!_tables.TryGetValue(tableKey, out var table))
            {
                table = new TableModel(schema, tableName, [], [], []);
                _tables[tableKey] = table;
            }

            var indexName = node.Name.Value;
            var isUnique = node.Unique;
            var isClustered = node.Clustered ?? false;
            var keys = node.Columns.Select(c => c.Column.MultiPartIdentifier.Identifiers.Last().Value).ToList();
            var incs = node.IncludeColumns.Select(c => c.MultiPartIdentifier.Identifiers.Last().Value).ToList();
            var filter = node.FilterPredicate != null ? GetNodeText(node.FilterPredicate) : null;

            table.Indexes.Add(new IndexModel(indexName, isUnique, false, isClustered, filter, keys, incs));

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(AlterTableAddTableElementStatement node)
        {
            var schema = node.SchemaObjectName.SchemaIdentifier?.Value ?? _defaultSchema;
            var tableName = node.SchemaObjectName.BaseIdentifier.Value;
            var tableKey = $"{schema}.{tableName}";

            if (!_tables.TryGetValue(tableKey, out var table))
            {
                table = new TableModel(schema, tableName, [], [], []);
                _tables[tableKey] = table;
            }

            if (node.Definition?.TableConstraints != null)
            {
                foreach (var constraint in node.Definition.TableConstraints)
                {
                    ProcessTableConstraint(table, constraint);
                }
            }

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateProcedureStatement node)
        {
            var schema = node.ProcedureReference.Name.SchemaIdentifier?.Value ?? _defaultSchema;
            var name = node.ProcedureReference.Name.BaseIdentifier.Value;
            var script = GetNodeText(node);
            var hash = AstNormalizer.ComputeNormalizedHash(script);

            _programmableObjects[$"{schema}.{name}"] = new ProgrammableObjectModel(
                DatabaseObjectType.StoredProcedure,
                schema,
                name,
                script,
                hash
            );

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateViewStatement node)
        {
            var schema = node.SchemaObjectName.SchemaIdentifier?.Value ?? _defaultSchema;
            var name = node.SchemaObjectName.BaseIdentifier.Value;
            var script = GetNodeText(node);
            var hash = AstNormalizer.ComputeNormalizedHash(script);

            _programmableObjects[$"{schema}.{name}"] = new ProgrammableObjectModel(
                DatabaseObjectType.View,
                schema,
                name,
                script,
                hash
            );

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateFunctionStatement node)
        {
            var schema = node.Name.SchemaIdentifier?.Value ?? _defaultSchema;
            var name = node.Name.BaseIdentifier.Value;
            var script = GetNodeText(node);
            var hash = AstNormalizer.ComputeNormalizedHash(script);

            _programmableObjects[$"{schema}.{name}"] = new ProgrammableObjectModel(
                DatabaseObjectType.UserDefinedFunction,
                schema,
                name,
                script,
                hash
            );

            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTriggerStatement node)
        {
            var schema = node.Name.SchemaIdentifier?.Value ?? _defaultSchema;
            var name = node.Name.BaseIdentifier.Value;
            var script = GetNodeText(node);
            var hash = AstNormalizer.ComputeNormalizedHash(script);

            _programmableObjects[$"{schema}.{name}"] = new ProgrammableObjectModel(
                DatabaseObjectType.Trigger,
                schema,
                name,
                script,
                hash
            );

            base.ExplicitVisit(node);
        }

        private void ProcessTableConstraint(TableModel table, ConstraintDefinition constraint)
        {
            if (constraint is UniqueConstraintDefinition uq)
            {
                var name = uq.ConstraintIdentifier?.Value ?? (uq.IsPrimaryKey ? $"PK_{table.TableName}" : $"UQ_{table.TableName}");
                var keys = uq.Columns.Select(c => c.Column.MultiPartIdentifier.Identifiers.Last().Value).ToList();
                table.Indexes.Add(new IndexModel(name, true, uq.IsPrimaryKey, uq.Clustered ?? uq.IsPrimaryKey, null, keys, []));
            }
            else if (constraint is ForeignKeyConstraintDefinition fk)
            {
                var refTableObj = fk.ReferenceTableName;
                var refTableName = refTableObj?.BaseIdentifier?.Value ?? "Unknown";
                var name = fk.ConstraintIdentifier?.Value ?? $"FK_{table.TableName}_{refTableName}";
                var parentCols = fk.Columns.Select(c => c.Value).ToList();
                var refSchema = refTableObj?.SchemaIdentifier?.Value ?? _defaultSchema;
                var refCols = fk.ReferencedTableColumns?.Select(c => c.Value).ToList() ?? [];

                var delAction = fk.DeleteAction == DeleteUpdateAction.NotSpecified ? "NO_ACTION" : fk.DeleteAction.ToString().ToUpperInvariant();
                var updAction = fk.UpdateAction == DeleteUpdateAction.NotSpecified ? "NO_ACTION" : fk.UpdateAction.ToString().ToUpperInvariant();

                table.ForeignKeys.Add(new ForeignKeyModel(
                    name,
                    table.Schema,
                    table.TableName,
                    parentCols,
                    refSchema,
                    refTableName,
                    refCols,
                    delAction,
                    updAction
                ));
            }
            else if (constraint is CheckConstraintDefinition ck)
            {
                var name = ck.ConstraintIdentifier?.Value ?? $"CK_{table.TableName}";
                var def = GetNodeText(ck.CheckCondition);
                table.CheckConstraints.Add(new CheckConstraintModel(name, def));
            }
        }

        private static int GetDefaultMaxLength(string dt) => dt switch
        {
            "bigint" => 8,
            "int" => 4,
            "smallint" => 2,
            "tinyint" => 1,
            "bit" => 1,
            "uniqueidentifier" => 16,
            "datetime" => 8,
            "smalldatetime" => 4,
            "date" => 3,
            "time" => 5,
            "datetime2" => 8,
            "datetimeoffset" => 10,
            "float" => 8,
            "real" => 4,
            "money" => 8,
            "smallmoney" => 4,
            "char" or "varchar" => 1,
            "nchar" or "nvarchar" => 2,
            "binary" or "varbinary" => 1,
            _ => 8
        };

        private static byte GetDefaultPrecision(string dt) => dt switch
        {
            "bigint" => 19,
            "int" => 10,
            "smallint" => 5,
            "tinyint" => 3,
            "bit" => 1,
            "decimal" or "numeric" => 18,
            "money" => 19,
            "smallmoney" => 10,
            "float" => 53,
            "real" => 24,
            _ => 0
        };

        private static byte GetDefaultScale(string dt) => dt switch
        {
            "decimal" or "numeric" => 0,
            "money" or "smallmoney" => 4,
            "time" or "datetime2" or "datetimeoffset" => 7,
            _ => 0
        };

        private static string CleanDefaultValue(string def)
        {
            var trimmed = def.Trim();
            while (trimmed.StartsWith('(') && trimmed.EndsWith(')'))
            {
                trimmed = trimmed[1..^1].Trim();
            }
            return trimmed;
        }

        private static string GetNodeText(TSqlFragment node)
        {
            var generator = new Sql160ScriptGenerator();
            generator.GenerateScript(node, out var script);
            return script.Trim();
        }
    }
}
