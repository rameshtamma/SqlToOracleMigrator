# Pipeline stages

This describes a recommended stage model. Your implementation may use different names; align as needed.

## Stage 0 — Preflight
- Validate connections, permissions, NLS settings (Oracle), timeouts
- Validate mapping rules
- Estimate volume (row counts, table sizes)

## Stage 1 — Inventory
- Enumerate source objects
- Capture dependencies (FKs, indexes, views)
- Produce an execution plan

## Stage 2 — DDL Generate
- Translate SQL Server types → Oracle types
- Translate defaults (see Oracle compatibility notes)
- Produce an ordered DDL script (create tables → constraints → indexes)

## Stage 3 — DDL Apply
- Apply DDL to Oracle
- Record applied objects and failures

## Stage 4 — Data Move
- Bulk copy table data
- **Stage-aware inserts**: never insert NULL into NOT NULL columns unless a correct default is provided
- Batch sizing configurable

## Stage 5 — Post Validation
- Row counts (source vs target)
- Sampling / spot checks
- Optional checksum or hash by key ranges

## Stage 6 — Finalize
- Create/enable constraints and indexes if deferred
- Generate final report & artifacts
- Mark run complete
