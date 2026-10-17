<div align="center">

[![Typing SVG](https://readme-typing-svg.demolab.com?font=Fira+Code&weight=700&size=30&duration=3000&pause=800&color=38BDF8&center=true&vCenter=true&width=900&lines=SQLDrift+%E2%80%94+Schema+Drift+%26+Safe+Migration+Inspector;Compare+SQL+Server+%7C+.bak+Backups+%7C+Views+%7C+Tables+%7C+Data+%7C+Stored+Procedures;Beyond+Compare-style+Visual+Diff+for+Databases)](https://github.com/basitaliyousafzai/DataBaseComparer)

<br/>

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![C# 14](https://img.shields.io/badge/C%23-14-239120?style=for-the-badge&logo=csharp&logoColor=white)](https://learn.microsoft.com/dotnet/csharp/)
[![SQL Server](https://img.shields.io/badge/SQL_Server-2017%2B-CC2927?style=for-the-badge&logo=microsoftsqlserver&logoColor=white)](https://www.microsoft.com/sql-server)
[![Azure SQL](https://img.shields.io/badge/Azure_SQL-Supported-0078D4?style=for-the-badge&logo=microsoftazure&logoColor=white)](https://azure.microsoft.com/products/azure-sql)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow?style=for-the-badge)](LICENSE)
[![PRs Welcome](https://img.shields.io/badge/PRs-Welcome-brightgreen?style=for-the-badge)](https://github.com/basitaliyousafzai/DataBaseComparer/pulls)
[![GitHub Stars](https://img.shields.io/github/stars/basitaliyousafzai/DataBaseComparer?style=for-the-badge&color=fbbf24)](https://github.com/basitaliyousafzai/DataBaseComparer/stargazers)

<br/>

**High-performance SQL Server schema drift inspector, semantic diff engine, safe idempotent migration generator, `.bak` backup comparer, and visual desktop application.**

[🚀 Quick Start](#-quick-start) · [📦 Installation](#-installation) · [🧩 Features](#-features) · [🗂 Compare .bak Backups](#-compare-bak-backups) · [🖥 Desktop App](#-desktop-application) · [⚙️ CLI Reference](#-cli-reference) · [🏗 Architecture](#-architecture)

</div>

---

## 📌 Table of Contents

- [Overview](#-overview)
- [Features](#-features)
- [Why SQLDrift](#-why-sqldrift)
- [Installation](#-installation)
- [Quick Start](#-quick-start)
- [Compare .bak Backups](#-compare-bak-backups)
- [Desktop Application](#-desktop-application)
- [CLI Reference](#-cli-reference)
- [Visual Web UI](#-visual-web-ui)
- [Migration Safety](#-migration-safety--tsql-guardrails)
- [Architecture](#-architecture)
- [CI/CD Integration](#-cicd-integration)
- [Testing](#-testing)
- [Roadmap](#-roadmap)
- [Contributing](#-contributing)
- [License](#-license)
- [Search Keywords](#-search-keywords-seo)

---

## 🔍 Overview

**SQLDrift (`sqldrift`)** is a single, cross-platform, high-performance tool that inspects schema drift between two SQL Server / Azure SQL databases, static `.sql` DDL scripts, portable JSON snapshots, or **`.bak` backup files**.

It renders rich color-coded terminal reports, interactive side-by-side visual diffs (web + desktop), and generates **transaction-wrapped, idempotent, non-destructive T-SQL migration scripts** plus reversible rollback scripts.

> Think of it as **Beyond Compare for SQL Server databases** — but purpose-built for schemas, views, tables, records, stored procedures, functions, triggers, indexes, foreign keys, and check constraints.

---

## ✨ Features

### 🧠 Universal Metadata Extraction
- **Live SQL Server Catalog** — queries `sys.*` views with fast normalization and collation control.
- **Offline AST Parsing** — uses the official `Microsoft.SqlServer.TransactSql.ScriptDom.TSql160Parser` to parse `.sql` scripts without a running database.
- **Portable JSON Snapshots** — source-generated, zero-allocation serialization for version control and CI/CD drift checks.
- **`.bak` Backup Restore & Compare** — restores backups to a temporary database automatically, extracts schema + data, compares, and cleans up.

### 🧮 Semantic Diffing & AST Normalization
- Compares **tables, columns, data types, lengths, nullability, defaults, computed columns, collation**.
- Compares **primary keys, clustered/non-clustered indexes, filtered indexes, included columns**.
- Compares **foreign keys** and **check constraints**.
- Compares **views, stored procedures, functions, triggers** using normalized AST comparison — ignoring whitespace, comments, and casing.
- Compares **table records (data)** row-by-row via deterministic hashing.

### 🛡 Safety Guardrails & Data-Loss Protection
- Classifies drift by severity: **Low** (additive), **Medium** (alterations/widening), **Breaking** (drops, narrowing, `NULL → NOT NULL`).
- Destructive operations are automatically **commented out** in guarded blocks unless `--allow-data-loss` is supplied.
- Transaction-wrapped execution with full `TRY/CATCH` and `ROLLBACK`.

### 🧭 Topological Dependency Planner (DAG)
- Solves multi-table and cyclic foreign-key relationships using **Kahn's Algorithm**.
- Executes migration in **9 clean phases**: Schemas → Base Tables → Columns & Secondary Indexes → Foreign Keys → Check Constraints → Functions → Views → Stored Procedures → Guarded Drops.

### 🖥 Visual Interfaces
- **Embedded Web UI** — ASP.NET Core Minimal API serving a responsive SPA with side-by-side diffs and selective patch generation.
- **Desktop Application (WPF)** — native Windows app to attach `.bak` files, compare, and generate migration scripts.

### 🏢 Enterprise CI/CD Integration
- Output formats: **ANSI console, JSON, JUnit XML, GitHub Flavored Markdown**.
- Exit codes for `--fail-on-drift` and `--fail-on-breaking`.
- GitHub Actions step summary support.

---

## 🤔 Why SQLDrift?

| Capability | SQLDrift |
|---|---|
| Live DB compare | ✅ |
| `.sql` script compare | ✅ |
| `.bak` backup compare | ✅ |
| Schema + data compare | ✅ |
| Views / SPs / Functions / Triggers | ✅ (AST-normalized) |
| Idempotent migration scripts | ✅ |
| Rollback scripts | ✅ |
| Visual diff (web + desktop) | ✅ |
| CI/CD exit codes | ✅ |
| Data-loss guardrails | ✅ |

---

## 📦 Installation

### 1. Install as a .NET Global Tool
```bash
dotnet tool install -g SqlDrift.Tool
```

### 2. Build from Source
```bash
git clone https://github.com/basitaliyousafzai/DataBaseComparer.git
cd DataBaseComparer
dotnet build
```

### 3. Run the Desktop App
```bash
dotnet run --project src/SqlDrift.Desktop/SqlDrift.Desktop.csproj
```

---

## 🚀 Quick Start

```bash
# Compare two live databases
sqldrift compare "Server=sql1;Database=DevDb;Integrated Security=true;" \
                 "Server=sql2;Database=ProdDb;Integrated Security=true;"

# Compare two .sql scripts and render Markdown
sqldrift compare ./schema_v2.sql ./schema_v1.sql --format markdown --out drift_report.md

# Generate safe migration + rollback scripts
sqldrift generate ./dev_schema.sql ./prod_schema.sql \
                  --out migration.sql \
                  --rollback-out rollback.sql \
                  --allow-data-loss

# Launch visual web UI
sqldrift ui ./source_schema.sql ./target_schema.sql --port 5050
```

---

## 🗂 Compare .bak Backups

SQLDrift can compare two (or more) SQL Server `.bak` backup files of the **same database**:

```bash
sqldrift compare-bak ./db_v1.bak ./db_v2.bak \
  --server "Server=localhost;Integrated Security=true;TrustServerCertificate=true;"
```

What happens:
1. Each `.bak` is **restored to a uniquely named temporary database** on the SQL Server instance.
2. Schema metadata **and table records** are extracted from both.
3. A full semantic diff (schema + data + programmable objects) is produced.
4. Temporary databases are **dropped automatically** after comparison.

> 💡 This is perfect for comparing production backups, point-in-time snapshots, or pre/post-deployment states **without touching your live databases**.

---

## 🖥 Desktop Application

A native **WPF desktop application** provides a visual, Beyond Compare-style workflow:

- Attach **two or more `.bak` files**.
- Select **source** and **target**.
- Restore, extract, and compare with one click.
- Browse drifted objects in a grid (tables, views, data, stored procedures, etc.).
- Generate and save an **idempotent migration SQL script**.
- View the generated script in-app.

```bash
dotnet run --project src/SqlDrift.Desktop/SqlDrift.Desktop.csproj
```

---

## ⚙️ CLI Reference

| Command | Description |
|---|---|
| `compare <source> <target>` | Compare two databases, scripts, or snapshots. |
| `compare-bak <bak1> <bak2>` | Compare two `.bak` backup files. |
| `generate <source> <target>` | Generate safe migration + rollback scripts. |
| `sync <source> <target>` | Alias for `generate` (supports `--apply`). |
| `snapshot <source>` | Export schema to a portable JSON snapshot. |
| `ci <source> <target>` | CI/CD gate with exit codes and reports. |
| `ui <source> <target>` | Launch embedded visual diff web UI. |

### Common Options
- `-f, --format` — `console`, `json`, `junit`, `markdown`
- `-o, --out` — write output to file
- `--fail-on-drift` / `--fail-on-breaking` — non-zero exit codes
- `--allow-data-loss` — permit destructive operations
- `--no-transaction` — disable transaction wrapping
- `--strict-collation` — enforce collation comparison

---

## 🌐 Visual Web UI

```bash
sqldrift ui ./source_schema.sql ./target_schema.sql --port 5050
```

- Side-by-side AST diffs
- Filter by severity and object type
- Search across objects
- Selective change inclusion
- Generate & download migration script
- Export JSON report

---

## 🛡 Migration Safety & T-SQL Guardrails

1. **Transaction Safety** — `BEGIN TRY … BEGIN TRANSACTION … COMMIT … END TRY BEGIN CATCH … ROLLBACK … END CATCH`.
2. **First-Statement Dynamic Execution** — `CREATE OR ALTER PROCEDURE/VIEW/FUNCTION/TRIGGER` wrapped in `EXEC(N'…')`.
3. **Strict Idempotency** — every alteration guarded with `IF NOT EXISTS (SELECT 1 FROM sys.…)`.
4. **Data-Loss Protection** — destructive ops commented out unless `--allow-data-loss`.
5. **Cyclic FK Resolution** — foreign keys decoupled from table creation (Phase 4).

---

## 🏗 Architecture

```
                          ┌──────────────────────┐
                          │   Universal Input    │
                          │ DB / .sql / .json / .bak │
                          └──────────┬───────────┘
                                     │
              ┌──────────────────────┴──────────────────────┐
              ▼                                             ▼
   ┌─────────────────────┐                     ┌─────────────────────┐
   │ LiveCatalogExtractor│                     │ ScriptDomExtractor  │
   │  (sys.* queries)    │                     │ (TSql160 AST)       │
   └──────────┬──────────┘                     └──────────┬──────────┘
              │                                           │
              └──────────────────┬────────────────────────┘
                                 ▼
                    ┌────────────────────────────┐
                    │ DatabaseSchemaSnapshot     │
                    │  (Tables, Data, Objects)   │
                    └────────────┬───────────────┘
                                 ▼
                    ┌────────────────────────────┐
                    │        SchemaDiffer        │
                    │  (Semantic + AST + Data)    │
                    └────────────┬───────────────┘
                                 ▼
                    ┌────────────────────────────┐
                    │      MigrationPlanner      │
                    │   (DAG / Kahn's Sort)      │
                    └────────────┬───────────────┘
                     ┌───────────┴───────────┐
                     ▼                       ▼
        ┌─────────────────────┐   ┌─────────────────────┐
        │ MigrationScriptGen  │   │ SqlDrift.Server/UI  │
        │ (Idempotent T-SQL)  │   │ (Web + Desktop)     │
        └─────────────────────┘   └─────────────────────┘
```

### Modules
- **`SqlDrift.Core`** — extraction, AST parsing, diff engine, DAG planner, generator, backup restore.
- **`SqlDrift.Cli`** — Spectre.Console commands.
- **`SqlDrift.Server`** — embedded ASP.NET Core web UI.
- **`SqlDrift.Desktop`** — WPF desktop application.
- **`SqlDrift.Core.Tests`** — xUnit test suite.

---

## 🔄 CI/CD Integration

```yaml
name: Schema Drift Check
on:
  pull_request:
    paths: ['database/**']

jobs:
  inspect-drift:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '10.0.x'
      - run: dotnet tool install -g SqlDrift.Tool
      - run: |
          sqldrift ci ./database/schema.sql ./snapshots/prod.json \
                     --fail-on-breaking \
                     --format markdown \
                     --out drift-summary.md
      - if: always()
        run: cat drift-summary.md >> $GITHUB_STEP_SUMMARY
```

---

## 🧪 Testing

```bash
dotnet test --configuration Release
```

Covers schema extraction, AST normalization, snapshot serialization, diff severity, DAG topological sort, backup restore helpers, and idempotent SQL generation.

---

## 🗺 Roadmap

- [ ] Selective table data diff with row-level preview
- [ ] Folder-to-folder `.sql` file comparison (Beyond Compare-style)
- [ ] PostgreSQL / MySQL support
- [ ] Native AOT single-file binaries
- [ ] Dark/light theme toggle in desktop app

---

## 🤝 Contributing

Contributions are welcome! Please open an issue or pull request.

1. Fork the repository
2. Create a feature branch
3. Commit your changes
4. Open a Pull Request

---

## 📄 License

This project is licensed under the [MIT License](LICENSE).

---

## 🔎 Search Keywords (SEO)

sql server schema diff, database schema comparison, sql drift detection, schema migration generator, idempotent tsql, sql backup comparer, bak file compare, database backup diff, sql server backup restore compare, view comparison, stored procedure diff, table data comparison, records comparison, beyond compare database, sql server tool, dotnet 10 cli, database devops, ci cd schema check, sql server dag migration, azure sql diff, database version control, sql schema inspector, safe migration script, rollback script generator, sql server desktop app

---

<div align="center">

⭐ If SQLDrift helps you, please star the repository! ⭐

[![GitHub](https://img.shields.io/badge/GitHub-basitaliyousafzai%2FDataBaseComparer-181717?style=for-the-badge&logo=github)](https://github.com/basitaliyousafzai/DataBaseComparer)

</div>