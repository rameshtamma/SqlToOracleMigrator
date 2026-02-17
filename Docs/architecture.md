# Architecture
```mermaid
flowchart LR
  UI[WPF Desktop] --> Engine[Migration Engine]
  Engine --> ToolMig[ToolMig Repository]
  Engine --> SQL[SQL Server]
  Engine --> ORA[Oracle]
  Engine --> Artifacts[Run Artifacts]
  ToolMig --> UI
```
