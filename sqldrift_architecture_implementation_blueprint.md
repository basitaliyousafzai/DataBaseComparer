# Product Specification & Implementation Blueprint: SQLDrift (`sqldrift`)

## 1. Executive Summary & Project Goal
**SQLDrift** is a high-performance, single-binary, cross-platform CLI and local-web utility built on **.NET 10** (`net10.0`) using C# 14. It inspects schema drift between two live SQL Server / Azure SQL databases or static `.sql` DDL scripts, renders side-by-side visual diffs, and generates safe, idempotent, non-destructive migration scripts (`IF NOT EXISTS...`) and reversal scripts.

This specification document provides an end-to-end blueprint for autonomous AI coding agents to construct the entire project from scratch.

---

## 2. Technical Stack & Dependencies

* **Target Framework:** `net10.0` (C# 14, nullable enabled, Native AOT compatible)
* **Project Structure:** Modular Single-Solution or Unified Multi-Project structure:
  * `SQLDrift.Core` (Extraction, AST Parsing, Dependency DAG, Diff Engine, Generator)
  * `SQLDrift.Cli` (Spectre.Console CLI, Entry Point, Minimal API Web Server)
* **Key NuGet Packages:**
  * `Microsoft.Data.SqlClient` (v5.2+ or latest for .NET 10)
  * `Microsoft.SqlServer.TransactSql.ScriptDom` (official T-SQL AST parser)
  * `Spectre.Console` & `Spectre.Console.Cli` (Terminal UI & CLI handling)
  * `DiffPlex` (text diff engine for stored procs, views, triggers)
  * `System.Text.Json` (AOT-compatible JSON serialization for snapshots)

---

## 3. Architecture & Data Contracts

### 3.1 Domain Models (`SQLDrift.Core.Models`)

```csharp
namespace SQLDrift.Core.Models;

public enum DatabaseObjectType
{
    Table,
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

public enum DriftType
{
    Unchanged,
    Added,       // Exists in Source, missing in Target
    Missing,     // Missing in Source, exists in Target
    Modified     // Exists in both but structural or definition differences exist
}

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
    string? DefaultValue,
    string? ComputedDefinition
);

public record IndexModel(
    string IndexName,
    bool IsUnique,
    bool IsPrimaryKey,
    string FilterDefinition,
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
    string OnDeleteAction,
    string OnUpdateAction
);

public record TableModel(
    string Schema,
    string TableName,
    List<ColumnModel> Columns,
    List<IndexModel> Indexes,
    List<ForeignKeyModel> ForeignKeys
);

public record ProgrammableObjectModel(
    DatabaseObjectType ObjectType,
    string Schema,
    string ObjectName,
    string DefinitionScript,
    string NormalizedHash
);

public record DatabaseSchemaSnapshot(
    string DatabaseName,
    DateTime ExtractedAtUtc,
    Dictionary<string, TableModel> Tables,
    Dictionary<string, ProgrammableObjectModel> ProgrammableObjects
);

public record ObjectDriftResult(
    DatabaseObjectType ObjectType,
    string Schema,
    string Name,
    DriftType DriftType,
    DriftSeverity Severity,
    string Description,
    string? SourceDefinition,
    string? TargetDefinition
);

public record SchemaDriftReport(
    string SourceIdentifier,
    string TargetIdentifier,
    DateTime AnalyzedAtUtc,
    List<ObjectDriftResult> Drifts
);
```

---

## 4. Module Specifications

### Module 1: Schema Extraction Engine (`SQLDrift.Core.Extractor`)

#### 1.1 `LiveCatalogExtractor.cs`
Queries SQL Server system metadata (`sys.tables`, `sys.columns`, `sys.types`, `sys.indexes`, `sys.index_columns`, `sys.foreign_keys`, `sys.foreign_key_columns`, `sys.sql_modules`, `sys.objects`).
* Normalizes character lengths (`-1` to `MAX`).
* Strips collation differences unless `--strict-collation` flag is enabled.
* Returns a strongly typed `DatabaseSchemaSnapshot`.

#### 1.2 `ScriptDomExtractor.cs`
Uses `Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser` to parse offline `.sql` schema files or directories.
* Traverses the AST (`TSqlFragmentVisitor`) to extract `CreateTableStatement`, `CreateProcedureStatement`, `CreateViewStatement`, and constraints into the same `DatabaseSchemaSnapshot` format.

#### 1.3 `SnapshotSerializer.cs`
* Implements `.SaveSnapshot(DatabaseSchemaSnapshot, string filePath)` and `.LoadSnapshot(string filePath)` using `.NET 10` Source-Generated System.Text.Json serializers for fast, zero-allocation AOT serialization.

---

### Module 2: Comparison & Semantic Diff Engine (`SQLDrift.Core.Diff`)

#### 2.1 `SchemaDiffer.cs`
* Compares `Source` snapshot against `Target` snapshot:
  * Identifies tables present in Source but missing in Target (`Added`).
  * Identifies tables missing in Source but present in Target (`Missing`).
  * Compares table columns: name, data type, length, nullability, defaults.
  * Compares indexes, primary keys, and foreign keys.
  * Compares programmable objects using normalized AST script comparison (ignoring comments, indentation, and casing differences).

#### 2.2 `SeverityEvaluator.cs`
Assigns `DriftSeverity`:
* Column added with `NULL` or default $\rightarrow$ `Low`
* Stored Procedure altered $\rightarrow$ `Medium`
* Column type converted (e.g. `VARCHAR(50)` to `VARCHAR(20)`) $\rightarrow$ `Breaking`
* Column or Table deleted $\rightarrow$ `Breaking`

---

### Module 3: Dependency Graph & Sorter (`SQLDrift.Core.Planner`)

#### 3.1 `DependencyGraph.cs`
* Implements a Directed Acyclic Graph (DAG) for database objects.
* Detects foreign-key dependencies between parent and child tables.
* Detects dependencies between tables and dependent views or indexed views.
* Uses **Kahn's Algorithm** for topological sorting to ensure:
  1. Base tables created first.
  2. Columns and Primary Keys added.
  3. Foreign keys and cross-table constraints bound second.
  4. Views, Functions, and Procedures created/altered last.

---

### Module 4: Safe Idempotent DDL Generator (`SQLDrift.Core.Generator`)

#### 4.1 `MigrationScriptGenerator.cs`
Generates non-destructive, safe T-SQL execution scripts wrapped in a transaction:
```sql
BEGIN TRY
    BEGIN TRANSACTION;

    -- Idempotent Table / Column Alterations
    IF NOT EXISTS (
        SELECT 1 FROM sys.columns c 
        JOIN sys.tables t ON c.object_id = t.object_id 
        JOIN sys.schemas s ON t.schema_id = s.schema_id 
        WHERE s.name = 'dbo' AND t.name = 'Customers' AND c.name = 'MiddleName'
    )
    BEGIN
        ALTER TABLE [dbo].[Customers] ADD [MiddleName] NVARCHAR(100) NULL;
        PRINT 'Added column [dbo].[Customers].[MiddleName]';
    END

    -- Programmable Objects with CREATE OR ALTER
    -- ...

    COMMIT TRANSACTION;
    PRINT 'Migration completed successfully.';
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    DECLARE @ErrMsg NVARCHAR(4000) = ERROR_MESSAGE();
    DECLARE @ErrSev INT = ERROR_SEVERITY();
    DECLARE @ErrState INT = ERROR_STATE();
    RAISERROR(@ErrMsg, @ErrSev, @ErrState);
END CATCH;
```
* **Destructive Operation Guard:** If an operation causes data loss (e.g. `DROP COLUMN`), emit a commented-out safety block and throw a warning unless `--allow-data-loss` is passed.

---

### Module 5: CLI & Local Web UI (`SQLDrift.Cli`)

#### 5.1 CLI Commands (Spectre.Console)
* `sqldrift compare <source> <target>`: Prints color-coded drift tree and severity matrix.
* `sqldrift generate <source> <target> --out migration.sql`: Generates idempotent update script.
* `sqldrift snapshot <source> --out snapshot.json`: Extracts schema to portable snapshot file.
* `sqldrift ci <source> <target> --fail-on-drift`: Exits with status code `1` if drift exists (for GitHub Actions / CI/CD).
* `sqldrift ui <source> <target> --port 5050`: Starts embedded ASP.NET Core Minimal API and opens browser to `http://localhost:5050`.

#### 5.2 Embedded Web UI
* Single-page responsive web UI served directly via embedded HTML/CSS/JS (no Node.js build step needed).
* Visual side-by-side diff (using DiffPlex and Monaco/Highlight.js styling).
* Checkbox tree allowing users to selectively include/exclude specific changes before downloading the SQL patch file.

---

## 5. Step-by-Step AI Agent Prompt

When instructing the AI agent to implement this repository, use the following prompt:

```text
You are an expert .NET 10 systems engineer and SQL Server architect.
Implement the complete 'SQLDrift' open-source solution adhering strictly to the architecture specification above.

Requirements:
1. Target: .NET 10 (C# 14). Enable nullable reference types and implicit usings.
2. Create standard modular structure:
   - src/SQLDrift.Core (Extractor, Models, Diff, Planner, Generator)
   - src/SQLDrift.Cli (Spectre.Console CLI commands, Web UI host)
   - tests/SQLDrift.Tests (xUnit tests for DAG topological sort, diffing, and idempotent SQL generation)
3. Write clean, memory-efficient C# code utilizing raw string literals, pattern matching, records, and spans where applicable.
4. Implement complete metadata extraction SQL queries for SQL Server 2017+ through SQL Server 2022/Azure SQL.
5. Provide a zero-dependency embedded web interface in SQLDrift.Cli using ASP.NET Core Minimal APIs serving a single responsive HTML file that displays diffs and generates scripts.
6. Include a production-ready README.md with SVG badges, architecture overview, installation guide (dotnet tool & single binary release), and GitHub Actions CI workflow file.
```

---

## 6. GitHub Repository Setup & Launch Checklist
* [ ] Add GitHub workflow (`.github/workflows/ci.yml`) targeting Ubuntu, Windows, and macOS with Native AOT compilation.
* [ ] Add GitHub Actions release workflow to publish cross-platform binaries on Git tag push.
* [ ] Pack as global tool: `dotnet pack -c Release` $\rightarrow$ publish to NuGet as `SQLDrift.Tool`.
* [ ] Topics / Tags for GitHub SEO: `sql-server`, `schema-diff`, `migration-tool`, `database-drift`, `dotnet10`, `cli-tool`, `devops`, `dba`, `ci-cd`.