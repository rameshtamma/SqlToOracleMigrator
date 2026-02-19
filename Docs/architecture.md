# Architecture

## Solution layout

- **SqlToOracleMigrator.Core**
  - Migration engine, stage runners, metadata providers, DDL composition, data movement utilities
- **SqlToOracleMigrator.Desktop**
  - WPF UI (MVVM-ish), connection management, run launch and monitoring
- **SqlToOracleMigrator.Tests**
  - NUnit tests (where available)

## Core components

### MigrationEngine (Core)

`MigrationEngine` is the orchestration root. It coordinates:

- **ConnectionManager** – opens, tracks, and closes SQL/Oracle connections
- **SqlServerMetadataProvider / OracleMetadataProvider** – discovers schema objects and types
- **SqlToOracleTypeMapper** – maps SQL types to Oracle types
- **DDL Composer** – generates Oracle DDL (tables, constraints, indexes)
- **Stage runners (v1.2)** – execute the migration pipeline

### Stage pipeline

The engine is organized into **stages** (runners are `IMigrationStageRunner`). Stages log progress, create artifacts, and can be resumed safely.

Typical stage flow:

1. **Discovery & planning** – inventory objects and compute migration plan/order
2. **DDL generation (dry run)** – produce Oracle DDL scripts
3. **Schema build / deployment skeleton** – create target tables and core objects
4. **Data prep / strategy** – decide migration strategy and sampling
5. **Parallel data migration** – copy table data with batching and error capture
6. **Post-load enforcement** – add constraints/indexes and FKs
7. **Final verification** – rowcount checks + certificate generation

### Reports and observability

Each run writes a **run directory** under the tool’s logs folder.

- JSON summary: `run_summary.json`
- Landing page: `RunSummary.html`
- Stage reports/errors: `*_report.txt/json`, `*_errors.txt/json`
- DDL artifacts: `SchemaBuild_DDL.sql/zip`
- Certificate: `ExecuteVerify_MigrationCertificate.json/pdf`
- Comparison: `SourceTargetComparison_report.txt/json`

### Design patterns

- **Pipeline / Stage Runner pattern** – stages are composable, resumable units
- **Partial class organization** – large engine split by concern
- **Repository pattern (ToolMigRepository)** – persists run artifacts/status (where configured)
- **Adapter/Provider pattern** – metadata and type mapping separated from orchestration

## Identifier policy

Oracle identifier handling is critical.

- When using **unquoted identifiers**, Oracle folds names to **UPPERCASE**.
- Quoted identifiers preserve case and must match exactly.

The tool supports a consistent policy (prefer unquoted uppercase). All generated DDL (tables, constraints, indexes, foreign keys) must follow the **same** policy.

## Bulk copy / parallelism notes

Oracle bulk loads can be sensitive to:

- **shared connections under parallel load**
- **type conversion edge cases** (`time`, intervals, special CLR/SqlTypes)

The long-term strategy is:

- prefer per-operation connections (or gated access when not possible)
- normalize problematic types before bulk operations
- fall back to safe insert-path when driver limitations are detected

