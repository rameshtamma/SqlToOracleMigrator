# Oracle compatibility notes

## Defaults: SQL Server → Oracle

A common mismatch is SQL Server expressions in `DEFAULT` constraints. Example patterns that need translation:

- SQL Server: `NEXT VALUE FOR dbo.MySeq`
- Oracle: `MySeq.NEXTVAL`

Also watch for SQL Server expressions like `CAST(...)`, `CONVERT(...)`, `GETDATE()`, `NEWID()`.

### Implemented / recommended mapping examples

- `NEXT VALUE FOR <seq>` → `<seq>.NEXTVAL`
- `GETDATE()` → `SYSDATE`
- `SYSUTCDATETIME()` → `SYSTIMESTAMP AT TIME ZONE 'UTC'` (or store as timestamp with timezone)
- `NEWID()` → `SYS_GUID()` (or a GUID-as-RAW/CHAR strategy)

> Prior work note: WWI migration DDL defaults were fixed by converting `NEXT VALUE FOR`/`CAST`/`CONVERT` to Oracle-friendly equivalents, including `SEQUENCE.NEXTVAL`.

## NOT NULL columns

Avoid generating inserts that provide NULL into NOT NULL columns.
Prefer:
- omit the column to allow default,
- or map to a correct derived value,
- or stage-aware projection based on target defaults.

## Identifiers

- Oracle identifier length limits may require truncation + stable hashing suffix.
- Preserve mapping dictionary so round-tripping is possible.

## Large objects

- CLOB/BLOB: prefer streaming/binding strategies; avoid loading entire payloads into memory.
