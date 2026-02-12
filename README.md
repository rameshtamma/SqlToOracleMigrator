# MigrationUpgradeV1

MigrationUpgradeV1 is a focused upgrade of the migration pipeline to improve **Oracle compatibility**, **stage-aware inserts**, and **diagnostics** for common Oracle runtime/DDL issues.

> **Status:** Documentation pack generated on February 12, 2026.

## What this project does

- Migrates data and schema artifacts from SQL Server to Oracle.
- Generates Oracle-friendly DDL and DML.
- Runs staged migration steps with resilient error handling and diagnostics.

## Notable recent fixes (from prior work)

- **WWI migration DDL defaults:** Converted SQL Server `NEXT VALUE FOR` / `CAST` / `CONVERT` defaults to Oracle `SEQUENCE.NEXTVAL`.
- **DataDefValidation diagnostics:** Improved reporting by including generated DDL in validation output.

## High-level architecture

- **UI / CLI (optional):** Orchestrates migrations, collects settings, triggers runs.
- **Core Engine:** Inventory, mapping, DDL generation, DML generation, stage runner.
- **Oracle Adapter:** Connection/transaction helpers, bulk copy, retry logic.
- **Diagnostics:** Structured logging, error classification, run summaries.

See `docs/architecture.md` for more.

## Quick start (template)

1. Configure connections (source SQL Server + target Oracle).
2. Run inventory (discover objects).
3. Generate Oracle DDL + apply.
4. Run data migration (stage-aware inserts, bulk copy).
5. Validate (row counts, sampling, checksum where available).

> If you want this README to match your repo exactly, drop in your current **solution folder structure** and **run command(s)** and I’ll tailor it.

## Repo map (template)

- `src/`
  - `MigrationUpgradeV1.Core/`
  - `MigrationUpgradeV1.Engine/`
  - `MigrationUpgradeV1.Oracle/`
  - `MigrationUpgradeV1.Cli/` (optional)
  - `MigrationUpgradeV1.Desktop/` (optional)
- `docs/`
- `scripts/`
- `tests/`

## How to contribute

See `docs/contributing.md`.
