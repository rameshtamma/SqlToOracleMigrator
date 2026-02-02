# SQL Server → Oracle Migration (WPF / .NET 8)

This solution is a **.NET 8 WPF** desktop application for **SQL Server → Oracle** migration.

## Projects

- **SqlToOracleMigrator.Core** (net8.0)
  - Connection models + validation
  - ConnectionManager (LRU eviction; max 2 active per engine)
  - Metadata providers (SQL Server / Oracle)
  - MigrationEngine (DDL generation + data copy + basic validation)
  - JSON stores (queries, mappings, connections)

- **SqlToOracleMigrator.Desktop** (net8.0-windows, WPF)
  - Main window: Connections panel, Combined Inventory, Logs
  - Connection wizard: SQL Server + Oracle connection UI (per mockups)
  - Migration wizard: launches end-to-end database migration
  - Wizard-style flow aligned with ToolDesignWizard.docx (Introduction → Source → Convert → Target → Move Data → Summary)

## Prerequisites

- Windows 10/11
- Visual Studio 2022 (17.8+) or later
- .NET 8 SDK
- Network access to your SQL Server and Oracle instances

## Build & run

1. Open `SqlToOracleMigrator.sln`.
2. Set **SqlToOracleMigrator.Desktop** as Startup Project.
3. Build + run.


### Command-line build

You can also build from a terminal:

- PowerShell: `./build.ps1 Release`
- CMD: `build.cmd Release`

## Configuration files

All configuration is stored under the app output folder:

```
Data/
  Config/
    appsettings.json
    auth_types.json
    connection_types.json
    datatype_mappings.json
    sqlqueries.json
  Connections/
    *.json
  Logs/
    yyyyMMdd/
      *.log
      run_*.json
```

### Password handling

- If **Save Password** is checked, the password is encrypted using **DPAPI (CurrentUser)**.
- If **Save Password** is unchecked, the password is never persisted; you will be prompted when connecting or starting a migration.

## Key behaviors (guardrails)

- **No background connections:** a connection is only opened on **Connect**. Saved connections are validated only when you test/save/connect.
- **Disconnect & Reset:** Disconnect disposes connection objects; Reset disconnects + re-tests + reconnects.
- **Active connection limits:** maximum **2** active SQL connections and **2** active Oracle connections. When exceeded, least-recently-used is evicted.
- **Inventory expansion is lazy:** object lists load only on expand and in bounded pages (`limits.maxRowsPerExpand`).
- **Identifiers are validated:** basic checks exist for Oracle schema/user identifiers.
 - **Oracle schema prefixes are unquoted by default:** avoids ORA-01918 caused by quoted usernames (e.g., "system").

## Known limitations

- Migration engine currently migrates **tables** (DDL + row copy). Other object types (views/procs/functions/etc.) are not converted yet.
- Oracle schema creation is not performed; the schema/user must already exist.
- Data copy is implemented as a straightforward row-by-row insert (batched commits). For very large databases you will likely enhance batching/array binding.

## Architecture and design patterns

This solution intentionally follows a **layered architecture** with **MVVM** in the WPF app:

- **Desktop (WPF) – MVVM**
  - *ViewModels* (e.g., `MainViewModel`, `ValidateMigrationViewModel`) expose state + async commands.
  - *Views* bind to ViewModels (no direct DB access in UI).
  - *AsyncRelayCommand* keeps UI responsive during I/O.

- **Core – services and providers**
  - `ConnectionManager` owns lifetime + caching (LRU eviction) for open SQL/Oracle connections.
  - `SqlServerMetadataProvider` / `OracleMetadataProvider` encapsulate metadata queries.
  - `InventoryService` composes providers to produce **Combined Inventory** summaries and paged object lists.
  - `MigrationEngine` orchestrates the end-to-end migration workflow.

### Combined Inventory (read-only)

- The top grid shows per-database (SQL) / per-service (Oracle) summary metrics.
- Expanding a row loads *paged* object details (virtualized DataGrid).
- Double-clicking a connection in the left tree will load inventory and expand the most relevant row so object details become visible.

### Post-Migration Validation

- Source DB list is loaded from the selected SQL connection (`sys.databases`).
- Schemas are loaded from the selected source database (`sys.schemas`) and are **selected by default**.
- Reports are written to the configured output folder and **Open Last Report** launches the latest JSON report via the default viewer.

### Key implementation notes

- **Credential prompts are skipped** when an active connection already exists.
- **Metadata access is best-effort** and uses non-DBA views where possible to work in restricted environments.
