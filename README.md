# SqlToOracleMigrator

A .NET 8 tool (Core engine + WPF desktop UI) to migrate a SQL Server database (schema + data) into an Oracle PDB/schema with a stage-based, resumable pipeline.

## What you get

- **Wizard-style UI** to manage connections and launch migrations
- **Stage pipeline (v1.2)** with clear logs and artifacts per run
- **Schema build** (DDL generation + deploy)
- **Parallel data migration** (with guardrails and deterministic logging)
- **Post-load enforcement** (constraints, indexes, foreign keys)
- **Final verification** (row-count spot checks + certificate PDF/JSON)
- **One-click run landing page:** `RunSummary.html` in the run folder

## Quick start

1. Build
   - Open the solution in Visual Studio 2022
   - Build `SqlToOracleMigrator.Desktop` (startup project)

2. Configure connections
   - Click **New Database Connection**
   - Create:
     - a SQL Server connection (source)
     - an Oracle connection (target) / PDB service

3. Run migration
   - Select a SQL database node and choose **Migrate data**
   - Monitor progress in the UI + log files

4. Review outputs
   - Use **Open Logs Folder**
   - Each run writes a dedicated run directory with artifacts.
   - Open `RunSummary.html` for a single-page index of all stage reports and outputs.

## Run artifacts (what to look at)

In the run folder:

- `RunSummary.html` – **single landing page** with links to stage reports/errors and key artifacts
- `run_summary.json` – structured run metadata
- `SchemaBuild_DDL.sql` / `SchemaBuild_DDL.zip` – generated DDL
- `*_report.txt/json` – per-stage reports
- `*_errors.txt/json` – per-stage error bundles
- `ExecuteVerify_MigrationCertificate.json/pdf` – final verification certificate
- `SourceTargetComparison_report.txt/json` – end-user friendly **source vs target comparison** (row counts, missing tables, mismatch list)

## Safe shutdown (Close / X)

The UI supports graceful shutdown:
- Clicking **Close** or the window **X** will:
  - cancel/stop background work best-effort
  - disconnect active SQL/Oracle connections
  - dispose logging/resources

## Test database script

For a larger test DB (bigger than MigMini):
- `Data/Scripts/CreateMigMax.sql`

It creates a `MigMax` database with multiple schemas, tables, constraints, indexes, views, procedures, triggers, and tens of thousands of rows (including `time` datatype coverage).

## Docs

- `docs/Architecture.md` – components, stages, design patterns
- `docs/Operations.md` – runbook, troubleshooting, performance tuning

