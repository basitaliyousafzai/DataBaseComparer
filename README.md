# SQLDrift (`sqldrift`)

[![CI](https://github.com/sqldrift/sqldrift/actions/workflows/ci.yml/badge.svg)](https://github.com/sqldrift/sqldrift/actions/workflows/ci.yml)
[![Target: .NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-brightgreen.svg)](https://github.com/sqldrift/sqldrift/pulls)

> **High-Performance SQL Server Schema Drift Inspector, Semantic Diff Engine, and Safe Idempotent Migration Generator.**

SQLDrift inspects schema drift between two live SQL Server / Azure SQL databases, static `.sql` DDL scripts, or JSON snapshots. It renders rich color-coded terminal reports, interactive side-by-side visual diffs via an embedded local web server, and generates transaction-wrapped, idempotent, non-destructive migration scripts (`IF NOT EXISTS...`, `CREATE OR ALTER...`) and reversible rollback scripts.

---

## 🌟 Key Features

* **Universal Metadata Extraction:**
  * **Live SQL Server Catalog:** Queries `sys.*` views with fast character length normalization and collation control.
  * **Offline AST Parsing:** Uses official `Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser` to parse offline `.sql` scripts without requiring a running database server.
  * **Portable JSON Snapshots:** Source-generated, zero-allocation serialization for instant version control and CI/CD drift checks.
* **Semantic Diffing & AST Normalization:**
  * Compares tables, columns, data types, nullability, defaults, primary keys, clustered/non-clustered indexes, filtered indexes, foreign keys, and check constraints.
  * AST-based comparison for Stored Procedures, Views, Functions, and Triggers—ignoring non-functional differences like whitespace, comments, and casing.
* **Safety Guardrails & Data-Loss Protection:**
  * Classifies drift by severity: `Low` (additive), `Medium` (alterations/widening), and `Breaking` (drops, type narrowing, `NULL` to `NOT NULL`).
  * Destructive operations (`DROP COLUMN`, `DROP TABLE`) are automatically commented out in guarded blocks unless `--allow-data-loss` is explicitly supplied.
* **Topological Dependency Planner (DAG):**
  * Solves multi-table and cyclic foreign-key relationships using **Kahn's Algorithm**.
  * Executes migration in 9 clean phases: Schemas $\rightarrow$ Base Tables $\rightarrow$ Columns & Secondary Indexes $\rightarrow$ Foreign Keys $\rightarrow$ Check Constraints $\rightarrow$ Functions $\rightarrow$ Views $\rightarrow$ Stored Procedures $\rightarrow$ Guarded Drops.
* **Embedded Visual Diff Web UI (`--ui`):**
  * Built-in ASP.NET Core Minimal API server serving a responsive SPA directly from embedded assets.
  * Side-by-side diffing, filtering by severity and object type, and selective patch generation without Node.js dependencies.
* **Enterprise CI/CD Integration:**
  * Output formats for ANSI console, JSON, JUnit XML (for pipeline test reporting), and GitHub Flavored Markdown (for `GITHUB_STEP_SUMMARY`).

---

## 🏗 Architecture Overview

```
                                    ┌────────────────────────┐
                                    │    Universal Input     │
                                    │ (Live DB / .sql / JSON)│
                                    └───────────┬────────────┘
                                                │
                     ┌──────────────────────────┴──────────────────────────┐
                     ▼                                                     ▼
        ┌─────────────────────────┐                           ┌─────────────────────────┐
        │   LiveCatalogExtractor  │                           │   ScriptDomExtractor    │
        │ (sys.* catalog queries) │                           │  (TSql160 AST Visitor)  │
        └────────────┬────────────┘                           └────────────┬────────────┘
                     │                                                     │
                     └──────────────────────────┬──────────────────────────┘
                                                ▼
                               ┌─────────────────────────────────┐
                               │     DatabaseSchemaSnapshot      │
                               │  (Tables, Objects, Constraints) │
                               └────────────────┬────────────────┘
                                                │
                                                ▼
                               ┌─────────────────────────────────┐
                               │          SchemaDiffer           │
                               │  (Semantic comparison & AST)    │
                               └────────────────┬────────────────┘
                                                │
                                                ▼
                               ┌─────────────────────────────────┐
                               │        MigrationPlanner         │
                               │    (DAG Topological Sorter)     │
                               └────────────────┬────────────────┘
                                                │
                     ┌──────────────────────────┴──────────────────────────┐
                     ▼                                                     ▼
        ┌─────────────────────────┐                           ┌─────────────────────────┐
        │ MigrationScriptGenerator│                           │     SqlDrift.Server     │
        │(Safe Idempotent T-SQL)  │                           │ (Embedded Web UI / Diff)│
        └─────────────────────────┘                           └─────────────────────────┘
```

### Clean Architecture Modules:
* **`SqlDrift.Core`**: Domain models, live extractor, AST parser, semantic diff engine, Kahn's algorithm dependency sorter, and idempotent script generator.
* **`SqlDrift.Cli`**: Spectre.Console command-line tool (`compare`, `generate`, `sync`, `snapshot`, `ci`, `ui`).
* **`SqlDrift.Server`**: Embedded ASP.NET Core web server serving responsive visual diffing and patch generation UI.
* **`SqlDrift.Core.Tests`**: Unit and integration test suite covering schema extraction, AST normalization, diffing, DAG cycles, and idempotent SQL generation.

---

## 🚀 Installation & Getting Started

### 1. Install as a .NET Global Tool
```bash
dotnet tool install -g SqlDrift.Tool
```

### 2. Build from Source
```bash
git clone https://github.com/sqldrift/sqldrift.git
cd sqldrift
dotnet build
```

---

## 💻 CLI Usage & Command Reference

### 1. `compare` — Inspect Drift Between Environments
Compares two databases, `.sql` schema files, or JSON snapshots:
```bash
# Compare two live databases
sqldrift compare "Server=sql1.corp;Database=Staging;Integrated Security=true;" \
                 "Server=sql2.corp;Database=Prod;Integrated Security=true;"

# Compare offline DDL scripts and render Markdown
sqldrift compare ./schema_v2.sql ./schema_v1.sql --format markdown --out drift_report.md

# Fail with non-zero exit code if breaking drift is detected
sqldrift compare ./staging_snapshot.json ./prod_snapshot.json --fail-on-breaking
```

### 2. `generate` (or `sync`) — Generate Safe Migration & Rollback Scripts
Generates idempotent, transaction-safe T-SQL migration scripts:
```bash
# Generate safe forward and rollback scripts
sqldrift generate ./dev_schema.sql ./prod_schema.sql \
                  --out migration.sql \
                  --rollback-out rollback.sql

# Allow destructive operations (DROP COLUMN, DROP TABLE)
sqldrift generate ./dev_schema.sql ./prod_schema.sql \
                  --out migration.sql \
                  --allow-data-loss

# Apply migration directly to a target database
sqldrift sync ./snapshot.json "Server=localhost;Database=DevDb;Integrated Security=true;" --apply
```

### 3. `snapshot` — Export Portable Schema Snapshot
Extracts metadata into a portable JSON snapshot:
```bash
sqldrift snapshot "Server=localhost;Database=SalesDb;Integrated Security=true;" -o sales_v1.json
sqldrift snapshot ./migrations_folder/ -o local_scripts.json
```

### 4. `ci` — Automated CI/CD Pipeline Gatekeeper
Automates drift verification inside CI pipelines (e.g., GitHub Actions, Azure DevOps):
```bash
sqldrift ci ./latest_schema.sql ./prod_snapshot.json \
           --fail-on-drift \
           --format junit \
           --out ./test-results/drift-results.xml
```

### 5. `ui` — Launch Embedded Visual Diff Web Interface
Starts an embedded local web server with side-by-side AST diffing and selective change generation:
```bash
sqldrift ui ./source_schema.sql ./target_schema.sql --port 5050
```

---

## 🛡️ Migration Safety & T-SQL Guardrails

All generated migration scripts adhere to the following strict safety protocols:

1. **Transaction Safety:** Enclosed in `BEGIN TRY ... BEGIN TRANSACTION ... COMMIT ... END TRY BEGIN CATCH ... ROLLBACK ... END CATCH` blocks with full error propagation via `RAISERROR`.
2. **First-Statement Dynamic Execution:** Statements that SQL Server requires to be the first in a batch (e.g. `CREATE OR ALTER PROCEDURE / VIEW / FUNCTION / TRIGGER`) are wrapped in dynamic `EXEC(N'...')` blocks to ensure transaction safety without breaking query batch boundaries.
3. **Strict Idempotency:** Every table alteration is guarded with `IF NOT EXISTS (SELECT 1 FROM sys.columns...)` or `IF NOT EXISTS (SELECT 1 FROM sys.tables...)`.
4. **Data-Loss Protection:** Destructive operations (`DROP COLUMN`, `DROP TABLE`) are commented out in guarded blocks unless `--allow-data-loss` is passed.
5. **Cyclic FK Resolution:** Foreign keys are decoupled from table creation and bound in Phase 4 after all dependent tables exist, resolving cyclic table references seamlessly.

---

## 🔄 GitHub Actions CI/CD Integration

Add SQLDrift to your GitHub Actions pipeline:

```yaml
name: Schema Drift Check

on:
  pull_request:
    paths:
      - 'database/**'

jobs:
  inspect-drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'

      - name: Install SQLDrift
        run: dotnet tool install -g SqlDrift.Tool

      - name: Inspect Schema Drift
        run: |
          sqldrift ci ./database/schema.sql ./snapshots/prod.json \
                     --fail-on-breaking \
                     --format markdown \
                     --out drift-summary.md

      - name: Publish Summary to PR
        if: always()
        run: cat drift-summary.md >> $GITHUB_STEP_SUMMARY
```

---

## 🧪 Running Tests

The test suite covers schema extraction, AST script equivalence, snapshot serialization, diff severity evaluation, Kahn's algorithm DAG sorting, and idempotent script generation:

```bash
dotnet test --configuration Release
```

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).
