# Architecture

## Goals

- Deterministic, repeatable migrations
- Oracle-compatible DDL/DML generation
- Stage-aware execution so failures are isolated and resumable
- Fast bulk movement for large tables
- Actionable diagnostics (what failed, where, why, what to do next)

## Logical components

### Orchestrator (CLI/UI)
- Collects configuration (connections, schema mapping, options)
- Kicks off a migration run
- Shows progress by stage
- Supports resume from a checkpoint

### Core Engine
- Inventory: discovers objects (tables/views/indexes/constraints/etc.)
- Mapping: schema/user/table/column transforms and overrides
- DDL generator: produces Oracle DDL (types, defaults, constraints)
- Data mover: moves data using bulk copy + stage-aware insert logic
- Validation: row counts, basic sampling, and optional checksums

### Oracle Adapter
- Connection / transaction helpers
- Bulk insert/copy abstraction
- Retry strategy for transient Oracle errors (configurable)
- Feature gates for version-specific Oracle behavior

### Diagnostics
- Structured logs (JSON recommended)
- Run summary (counts, throughput, failures)
- “Most common errors” rollup and stage attribution

## Data flow

1. **Inventory** → build migration plan
2. **DDL generation** → apply Oracle schema
3. **Data migration** → bulk copy table data
4. **Validation** → confirm integrity
5. **Artifacts** → persist run logs, generated SQL, reports

## Design patterns suggested

- Strategy pattern for DDL/DML translators (SQL Server → Oracle)
- Pipeline / chain-of-responsibility for stage execution
- Adapter pattern for Oracle provider specifics
- Retry policy as a pluggable policy object
- Dependency Injection for testability and environment isolation
