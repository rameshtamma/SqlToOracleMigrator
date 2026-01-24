namespace SqlToOracleMigrator.Core;

/// <summary>
/// Minimal FK definition extracted from SQL Server to create Oracle FK constraints.
/// Deployed after data migration to avoid load failures.
/// </summary>
public sealed record SqlForeignKeyDef(
    string Schema,
    string Name,
    string TableSchema,
    string TableName,
    string RefTableSchema,
    string RefTableName,
    IReadOnlyList<SqlForeignKeyColumnPair> Columns,
    string? OnDeleteAction);

public sealed record SqlForeignKeyColumnPair(
    string ColumnName,
    string RefColumnName,
    int Ordinal);
