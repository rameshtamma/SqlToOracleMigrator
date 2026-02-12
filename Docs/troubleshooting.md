# Troubleshooting

## ORA-01400: cannot insert NULL into (...) (NOT NULL column)
Likely cause:
- stage-aware insert is projecting NULL into a NOT NULL target column.
Fix:
- omit the column when a default exists
- compute a mapped value
- ensure DDL default translation is correct

## ORA-00904: invalid identifier
Likely cause:
- name truncation or quoting mismatch
Fix:
- check identifier normalization rules
- ensure mapping dictionary is consistent
- verify reserved word quoting

## ORA-12899: value too large for column
Likely cause:
- data length exceeds target column size
Fix:
- review type mapping (NVARCHAR/VARCHAR2 lengths, BYTE vs CHAR semantics)
- increase column size or transform data

## ORA-00001: unique constraint violated
Likely cause:
- duplicates in source or ordering issues when constraints enabled early
Fix:
- defer constraints/indexes until after load
- validate uniqueness in source before load

## Timeouts / slow loads
Fixes:
- tune batch size and commit frequency
- enable array binding / bulk copy options
- run multiple tables in parallel with a cap

## Where to look
- `run-summary.json`
- `errors.jsonl` (filter by ORA code)
- `generated-ddl/` and `generated-dml/`
