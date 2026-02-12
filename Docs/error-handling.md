# Error handling & diagnostics

## Principles

- Fail fast on configuration errors
- Isolate failures by stage
- Provide a **single clear root cause** whenever possible
- Offer remediation hints (what to change, where)

## Error classification (suggested)

- Connection/auth
- Permission/privilege
- DDL translation
- DDL apply
- Data conversion (types)
- Not-null/defaults mismatch
- Constraint violations
- Timeouts / transient network issues
- Provider/library exceptions

## Stage attribution

Every error event should include:
- stage name
- object (schema.table)
- operation (DDL/DML)
- SQL/DDL snippet (safe-truncated)
- Oracle error code (e.g., ORA-xxxxx)
- recommended remediation

## Artifacts

Write per-run artifacts:
- `run-summary.json`
- `errors.jsonl`
- `generated-ddl/` (per object or consolidated)
- `generated-dml/` (optional)
- `validation-report.json`
