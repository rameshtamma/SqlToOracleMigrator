# Contributing

## Branching
- `main`: stable
- `dev`: active development
- feature branches: `feature/<name>`

## Coding conventions
- Keep translation logic isolated and testable
- Prefer pure functions for type/default mappings
- Keep Oracle provider calls behind an adapter

## Testing
- Unit tests: translators (DDL/DML), mapping, parsing
- Integration tests: Oracle container or test DB where feasible
- Regression tests: known ORA failures should have test cases

## Pull request checklist
- [ ] Added/updated tests
- [ ] Updated changelog
- [ ] Verified migration against a sample schema
- [ ] Captured run artifacts for comparison
