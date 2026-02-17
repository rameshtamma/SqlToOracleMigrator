# SqlToOracleMigrator — Consolidated Guide (latest)

Updated: **2026-02-17**

This guide consolidates:
- architecture & flows
- phases/stages
- configuration & artifacts
- operational runbook
- design patterns & considerations
- **appendix: ORA/SQL issues addressed so far** (tabular)
- known limitations & DBA playbook notes

---

## 1) Architecture

```mermaid
flowchart LR
  UI[WPF Desktop] -->|MigrationRequest| ENG[Core Migration Engine]
  ENG --> TM[ToolMig Repository]
  ENG --> SQL[SQL Server Source]
  ENG --> ORA[Oracle Target]
  ENG --> ART[Artifacts (files + RunArtifacts)]
  TM --> UI
  ART --> UI
```

### Execution model
- A run is a sequence of **stages** grouped into **phases**.
- ToolMig persists:
  - stage status (per stage)
  - object status (per object within stage)
- Artifacts are written to the run directory and/or persisted via RunArtifacts.

### Resume semantics (critical)
Resume skips objects in terminal states:
- `Completed`, `Skipped`, `Success`, `Succeeded`

This was added to stop repeated reruns of known non-blocking issues (e.g., XML indexes, wide indexes, T‑SQL views).

---

## 2) Phases & stages

### Phase 1 — Assess & Plan (Stages 1–3)
- Stage 1: connection validation + discovery bootstrap
- Stage 2: inventory extraction
- Stage 3: dependency DAG (Kahn’s algorithm) + complexity score  
  Score = (Rows * Cols) + (Indexes * 2) + (LOBs * 10)

### Phase 2 — Schema Build (Stages 4–6)
- Stage 4: provisioning/reset (idempotent DDL; CASCADE PURGE where appropriate)
- Stage 5: DDL validation
  - **parse-only validates TABLE + SEQUENCE**
  - views/procs/triggers are **deferred** (reported, not blocking)
- Stage 6: DeploymentSkeleton
  - tables first
  - safe constraints/indexes
  - view/proc/trigger deployment deferred/skipped (terminal) to prevent retries

### Phase 3 — Data Prep (Stages 7–8)
- Stage 7: sampling + staging preparation (NOT NULL/date risk detection)
- Stage 8: parallel bulk copy

### Phase 4 — Execute & Verify (Stages 9–10)
- Stage 9: post-load enforcement (constraints, conversions, stats)
- Stage 10: verification + certificate output (JSON/PDF depending on package)

---

## 3) Configuration & artifacts

### Required runtime files
Ensure these exist and are copied to the build output:
- `Data/Config/sqlqueries.json`
- `Data/Config/datatype_mappings.json`
- (optional) other jsons referenced by the UI/runners

**If you get runtime “missing sqlqueries.json”**:
- add repo-root copy-to-output rules (e.g., Directory.Build.targets) that copy `Data/**` into `$(TargetDir)Data\...`

### datatype_mappings.json rules (important)
Length-required Oracle types must include parentheses placeholders:
- `VARCHAR2({len})`, `NVARCHAR2({len})`, `CHAR({len})`, `NCHAR({len})`, `RAW({len})`
- Use precision/scale template for NUMBER as needed.

### Typical artifacts
- `SchemaBuild_DDL.sql`
- `SchemaBuild_DDLValidation.json`
- `DeploymentSkeleton_errors.json/txt`
- `DdlGenerationDryRun_errors.json/txt`
- `ExecuteVerify_MigrationCertificate.json/pdf`

---

## 4) Operational runbook

### Baseline (recommended)
Start with a minimal SQL Server DB (`MigMini`) before AdventureWorks:
1) create `MigMini`
2) run phases 1–4 end-to-end
3) confirm schema, sample row counts, and artifacts

### Large DBs (AdventureWorks/WWI)
Expect non-convertible objects (views/procs/triggers). The tool is designed to:
- deploy tables & safe schema items
- **defer/skip terminal** objects that require manual conversion
- keep moving (avoid repeat failures)

---

## 5) Design patterns & considerations

### Patterns
- Pipeline stage runner
- Repository (ToolMig)
- Strategy (staging, index remediation decisions)
- Idempotent execution (DDL ignore list)
- Observability (Serilog + per-object tracking + artifacts)

### Reliability choices implemented
- **No `DBMS_SQL.PARSE`** in DDL executor: switched to `EXECUTE IMMEDIATE` wrapper
- Non-blocking ORA handling is object-type aware (indexes vs views vs constraints)
- Terminal SKIP avoids infinite retry loops

---

## 6) Known limitations

- SQL Server **views/procs/functions/triggers** are not fully auto-converted from T‑SQL to PL/SQL (deferred).
- SQL Server XML indexes do not map 1:1 to Oracle B‑tree indexes. These can cause ORA‑02327 and require DBA remediation.
- Wide composite indexes can cause ORA‑01450 and typically require DBA redesign (prefix / function-based / reduced column set).

---

## 7) DBA remediation notes (quick)

### ORA-01450 (max key length)
Options:
- reduce indexed columns
- function-based prefix index (SUBSTR)
- hash column indexing
- Oracle Text for long text patterns

### ORA-02327 (LOB/XML expression index)
Options:
- relationalize searchable fields
- Oracle Text / XML DB approaches
- defer indexing until hot paths identified

### ORA-24344 (success with compilation error)
- object created INVALID; use USER_ERRORS/ALL_ERRORS to remediate
- often due to unconverted T‑SQL bodies

---

## 8) Appendix — Issues addressed so far (tabular)

| Category | Error | Typical Cause | Implemented Handling | Stage/Phase |
|---|---|---|---|---|
| Config | missing sqlqueries.json | Data folder not copied | Data/Config + copy-to-output | Startup |
| Mapping | ORA-00906 | type missing parentheses | mapping templates/guard | Phase2/Stage5 |
| Index | ORA-01450 | index key too long | non-blocking + terminal skip | Phase2/Stage6 |
| Index | ORA-02327 | LOB/XML expression index | non-blocking + terminal skip | Phase2/Stage6 |
| DDL | ORA-00955 | name exists | ignore (idempotent) | Phase2/Stage6 |
| Index | ORA-01408 | already indexed | ignore (idempotent) | Phase2/Stage6 |
| PK/UK | ORA-02260 | PK exists | ignore/skip | Phase2/Stage6 |
| PK/UK | ORA-02261 | unique exists | ignore/skip | Phase2/Stage6 |
| Views | ORA-00933 | T‑SQL view/proc | defer/skip terminal | Phase2/Stage6 |
| Views | ORA-00942 | missing dependency | defer/skip terminal (views) | Phase2/Stage6 |
| Triggers | ORA-04071 | SQL Server trigger form | defer/stub | Phase2/Stage5/6 |
| Compile | ORA-24344 | invalid object | non-blocking; report | Phase2/Stage6 |
| Wrapper | ORA-06550 / PLS-00103 | brittle wrapper | EXECUTE IMMEDIATE wrapper | Phase2/Stage6 |
| Data | ORA-01400 | NULL to NOT NULL | stage-aware projection | Phase3 |
| Data | ORA-01843 | invalid month | sampling/staging strategy | Phase3 |
| Discovery | Decimal→Double warnings | unsafe cast | ToNullableDouble helper | Phase1 |

---

## 9) Note requested
`solutions.csv: Your ORA-01450 suggestion (clamping/analyzer) is appropriate.`
