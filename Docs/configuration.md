# Configuration

## Recommended config model

Use a single configuration root with sections. Example (YAML-like):

- `source`: SQL Server connection info
- `target`: Oracle connection info
- `mapping`: schema/table/column mapping rules
- `pipeline`: enabled stages, resume point, parallelism
- `bulkCopy`: batch size, commit frequency, array binding size
- `diagnostics`: log folder, log format, verbosity

## Environment separation

- `config.dev.json`
- `config.uat.json`
- `config.prod.json`

## Secrets

Store secrets using a secure mechanism (e.g., DPAPI / Secret store) and reference them via key IDs, not plaintext.

## Resume / checkpoints

Persist:
- last completed stage
- last completed table
- last committed batch/offset

So the run can resume safely after failures.
