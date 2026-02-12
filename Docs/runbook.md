# Operations runbook

## Before you run
- Confirm Oracle user privileges (create table, create sequence, etc.)
- Confirm target tablespace sizing
- Confirm network stability and timeouts
- Confirm logging path has enough disk

## Running a migration (checklist)
1. Preflight
2. Inventory
3. Generate DDL
4. Apply DDL
5. Load data
6. Validate
7. Finalize

## Resume procedure
- Identify the last completed stage from `run-summary.json`
- Resume from next stage
- If a table failed mid-load, resume from last committed batch for that table

## Post-run
- Review validation report
- Review top errors and remediation suggestions
- Archive run artifacts for auditability
