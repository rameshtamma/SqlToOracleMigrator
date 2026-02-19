# Operations and Troubleshooting

## Where logs and artifacts go

From the UI click **Open Logs Folder**. Each run writes a subfolder that contains:

- `RunSummary.html` (start here)
- `run_summary.json`
- `*_report.*` and `*_errors.*`
- `SchemaBuild_DDL.*`
- `ExecuteVerify_MigrationCertificate.*`
- `SourceTargetComparison_report.*`

## How to read `RunSummary.html`

`RunSummary.html` is the single-page index for an entire run:

- Key artifacts at the top
- Stage reports/errors grouped by stage
- A list of all files for deep dives

## Source vs Target comparison report

`SourceTargetComparison_report.txt/json` is meant for end users who want answers without querying the databases.

It includes:

- total source rows vs total target rows
- missing target tables
- per-table rowcount deltas
- prioritized mismatch list (top issues first)

If mismatches are found, the report recommends:

- re-running the Parallel Data Migration stage for the impacted tables
- reviewing that table’s stage error bundle

## Common failure patterns

### ORA-00942 table or view does not exist

Usually indicates identifier mismatch or schema mapping mismatch.

Fixes:

- ensure schema build created the table in the expected target schema
- ensure constraints/indexes/FKs use the same identifier policy as table DDL

### ORA-00904 invalid identifier

Often due to quoted-vs-unquoted column name mismatch.

Fix:

- enforce consistent identifier formatting for table + column references in DDL

### Bulk copy failures / ODP.NET internal errors

Bulk copy can fail due to driver limitations:

- parallel bulk load on shared connections
- problematic conversions (e.g., SQL `time`)

Mitigations:

- use per-operation Oracle connections (or gate shared connection use)
- normalize problematic column projections
- fall back to safe insert path when bulk copy cannot safely proceed

## Performance tuning

- Keep reasonable DegreeOfParallelism (DOP) based on:
  - Oracle session limits
  - network latency
  - target I/O throughput
- Prefer batching sized to your environment
- Avoid `COUNT(*)` verification on extremely large tables unless required

## Safe shutdown

Close the app via **Close** button or window **X**.

The app will:

- unsubscribe from log/progress events
- disconnect active connections best-effort
- dispose logging resources

