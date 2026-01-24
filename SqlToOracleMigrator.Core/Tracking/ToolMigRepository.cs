using Microsoft.Data.SqlClient;

namespace SqlToOracleMigrator.Core.Tracking;

/// <summary>
/// ToolMig tracking repository stored in the SOURCE SQL Server database (ToolMig schema).
/// Enables stage-by-stage persistence, object-level status, and true resume across reruns.
/// </summary>
public sealed class ToolMigRepository
{
    private readonly IAppLogger _logger;

    public ToolMigRepository(IAppLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task EnsureCreatedAsync(SqlConnection openSql, CancellationToken cancellationToken)
    {
        if (openSql is null) throw new ArgumentNullException(nameof(openSql));

        var ddl = @"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = 'ToolMig')
    EXEC('CREATE SCHEMA ToolMig');

IF OBJECT_ID('ToolMig.Runs') IS NULL
BEGIN
    CREATE TABLE ToolMig.Runs
    (
        RunId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
        SourceDatabase NVARCHAR(256) NOT NULL,
        TargetDatabase NVARCHAR(256) NULL,
        Version INT NOT NULL,
        StartedAt DATETIMEOFFSET NOT NULL,
        EndedAt DATETIMEOFFSET NULL,
        Status NVARCHAR(32) NOT NULL,
        RequestJson NVARCHAR(MAX) NULL
    );
    CREATE INDEX IX_ToolMig_Runs_SourceDb ON ToolMig.Runs(SourceDatabase, Version);
END;

IF OBJECT_ID('ToolMig.StageStatus') IS NULL
BEGIN
    CREATE TABLE ToolMig.StageStatus
    (
        RunId UNIQUEIDENTIFIER NOT NULL,
        Stage NVARCHAR(64) NOT NULL,
        Status NVARCHAR(32) NOT NULL,
        StartedAt DATETIMEOFFSET NULL,
        EndedAt DATETIMEOFFSET NULL,
        ErrorCount INT NOT NULL DEFAULT(0),
        Message NVARCHAR(4000) NULL,
        CONSTRAINT PK_ToolMig_StageStatus PRIMARY KEY (RunId, Stage),
        CONSTRAINT FK_ToolMig_StageStatus_Runs FOREIGN KEY (RunId) REFERENCES ToolMig.Runs(RunId)
    );
    CREATE INDEX IX_ToolMig_StageStatus_RunId_Status ON ToolMig.StageStatus(RunId, Status);
END;

IF OBJECT_ID('ToolMig.ObjectStatus') IS NULL
BEGIN
    CREATE TABLE ToolMig.ObjectStatus
    (
        RunId UNIQUEIDENTIFIER NOT NULL,
        Stage NVARCHAR(64) NOT NULL,
        SchemaName NVARCHAR(256) NOT NULL,
        ObjectName NVARCHAR(256) NOT NULL,
        ObjectType NVARCHAR(64) NOT NULL,
        Status NVARCHAR(32) NOT NULL,
        StartedAt DATETIMEOFFSET NULL,
        EndedAt DATETIMEOFFSET NULL,
        ErrorCode NVARCHAR(64) NULL,
        ErrorMessage NVARCHAR(4000) NULL,
        CONSTRAINT PK_ToolMig_ObjectStatus PRIMARY KEY (RunId, Stage, SchemaName, ObjectName, ObjectType),
        CONSTRAINT FK_ToolMig_ObjectStatus_Runs FOREIGN KEY (RunId) REFERENCES ToolMig.Runs(RunId)
    );
    CREATE INDEX IX_ToolMig_ObjectStatus_RunId_Stage_Status ON ToolMig.ObjectStatus(RunId, Stage, Status);
END;

-- Upgrade path: earlier versions had PK without ObjectType, which prevents tracking non-table objects
IF OBJECT_ID('ToolMig.ObjectStatus') IS NOT NULL
BEGIN
    DECLARE @pk SYSNAME;
    DECLARE @pkIndexId INT;
    SELECT TOP(1) @pk = kc.name, @pkIndexId = kc.unique_index_id
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID('ToolMig.ObjectStatus') AND kc.type = 'PK';

    IF @pk IS NOT NULL AND @pkIndexId IS NOT NULL
    BEGIN
        IF NOT EXISTS (
            SELECT 1
            FROM sys.index_columns ic
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE ic.object_id = OBJECT_ID('ToolMig.ObjectStatus')
              AND ic.index_id = @pkIndexId
              AND c.name = 'ObjectType'
        )
        BEGIN
            EXEC('ALTER TABLE ToolMig.ObjectStatus DROP CONSTRAINT [' + @pk + ']');
            EXEC('ALTER TABLE ToolMig.ObjectStatus ADD CONSTRAINT PK_ToolMig_ObjectStatus PRIMARY KEY (RunId, Stage, SchemaName, ObjectName, ObjectType)');
        END
    END
END;
";
        await using var cmd = new SqlCommand(ddl, openSql);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ToolMigRunInfo?> GetRunAsync(SqlConnection openSql, Guid runId, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT RunId, SourceDatabase, TargetDatabase, Version, StartedAt, EndedAt, Status, RequestJson
FROM ToolMig.Runs
WHERE RunId = @runId;
";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@runId", runId);

        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        if (!await rdr.ReadAsync(cancellationToken)) return null;

        return new ToolMigRunInfo
        {
            RunId = rdr.GetGuid(0),
            SourceDatabase = rdr.GetString(1),
            TargetDatabase = rdr.IsDBNull(2) ? null : rdr.GetString(2),
            Version = rdr.GetInt32(3),
            StartedAt = rdr.GetFieldValue<DateTimeOffset>(4),
            EndedAt = rdr.IsDBNull(5) ? null : rdr.GetFieldValue<DateTimeOffset>(5),
            Status = rdr.GetString(6),
            RequestJson = rdr.IsDBNull(7) ? null : rdr.GetString(7)
        };
    }

    public async Task<IReadOnlyList<ToolMigRunInfo>> ListRunsAsync(SqlConnection openSql, string sourceDatabase, CancellationToken cancellationToken, int top = 50)
    {
        const string sql = @"
SELECT TOP (@top) RunId, SourceDatabase, TargetDatabase, Version, StartedAt, EndedAt, Status, RequestJson
FROM ToolMig.Runs
WHERE SourceDatabase = @db
ORDER BY Version DESC;
";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@top", top);
        cmd.Parameters.AddWithValue("@db", sourceDatabase);

        var list = new List<ToolMigRunInfo>();
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            list.Add(new ToolMigRunInfo
            {
                RunId = rdr.GetGuid(0),
                SourceDatabase = rdr.GetString(1),
                TargetDatabase = rdr.IsDBNull(2) ? null : rdr.GetString(2),
                Version = rdr.GetInt32(3),
                StartedAt = rdr.GetFieldValue<DateTimeOffset>(4),
                EndedAt = rdr.IsDBNull(5) ? null : rdr.GetFieldValue<DateTimeOffset>(5),
                Status = rdr.GetString(6),
                RequestJson = rdr.IsDBNull(7) ? null : rdr.GetString(7)
            });
        }
        return list;
    }

    public async Task<ToolMigRunInfo> CreateNewRunAsync(
        SqlConnection openSql,
        string sourceDatabase,
        string? targetDatabase,
        string? requestJson,
        CancellationToken cancellationToken)
    {
        var version = await GetNextVersionAsync(openSql, sourceDatabase, cancellationToken);
        var runId = Guid.NewGuid();
        var started = DateTimeOffset.Now;

        const string sql = @"
INSERT INTO ToolMig.Runs (RunId, SourceDatabase, TargetDatabase, Version, StartedAt, Status, RequestJson)
VALUES (@runId, @srcDb, @tgtDb, @version, @startedAt, @status, @requestJson);
";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@srcDb", sourceDatabase);
        cmd.Parameters.AddWithValue("@tgtDb", (object?)targetDatabase ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@version", version);
        cmd.Parameters.AddWithValue("@startedAt", started);
        cmd.Parameters.AddWithValue("@status", "Running");
        cmd.Parameters.AddWithValue("@requestJson", (object?)requestJson ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        _logger.Info($"[ToolMig] Created new run {runId} (v{version}) for DB '{sourceDatabase}'.");
        return new ToolMigRunInfo
        {
            RunId = runId,
            SourceDatabase = sourceDatabase,
            TargetDatabase = targetDatabase,
            Version = version,
            StartedAt = started,
            Status = "Running",
            RequestJson = requestJson
        };
    }

    private static async Task<int> GetNextVersionAsync(SqlConnection openSql, string sourceDatabase, CancellationToken cancellationToken)
    {
        const string sql = "SELECT ISNULL(MAX(Version), 0) FROM ToolMig.Runs WHERE SourceDatabase = @db;";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@db", sourceDatabase);
        var max = (int)(await cmd.ExecuteScalarAsync(cancellationToken) ?? 0);
        return max + 1;
    }

    public async Task UpsertStageAsync(SqlConnection openSql, Guid runId, string stage, string status, string? message, int errorCount, CancellationToken cancellationToken)
    {
        const string sql = @"
MERGE ToolMig.StageStatus AS t
USING (SELECT @runId AS RunId, @stage AS Stage) AS s
ON (t.RunId = s.RunId AND t.Stage = s.Stage)
WHEN MATCHED THEN
    UPDATE SET Status=@status,
               StartedAt = COALESCE(t.StartedAt, CASE WHEN @status IN ('InProgress','Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() END),
               EndedAt = CASE WHEN @status IN ('Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() ELSE NULL END,
               ErrorCount=@errorCount,
               Message=@message
WHEN NOT MATCHED THEN
    INSERT (RunId, Stage, Status, StartedAt, EndedAt, ErrorCount, Message)
    VALUES (@runId, @stage, @status,
            CASE WHEN @status IN ('InProgress','Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() ELSE NULL END,
            CASE WHEN @status IN ('Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() ELSE NULL END,
            @errorCount, @message);
";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@stage", stage);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@errorCount", errorCount);
        cmd.Parameters.AddWithValue("@message", (object?)message ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertObjectAsync(
        SqlConnection openSql,
        Guid runId,
        string stage,
        string schemaName,
        string objectName,
        string objectType,
        string status,
        string? errorCode,
        string? errorMessage,
        CancellationToken cancellationToken)
    {
        const string sql = @"
MERGE ToolMig.ObjectStatus AS t
USING (SELECT @runId AS RunId, @stage AS Stage, @schema AS SchemaName, @obj AS ObjectName, @objType AS ObjectType) AS s
ON (t.RunId=s.RunId AND t.Stage=s.Stage AND t.SchemaName=s.SchemaName AND t.ObjectName=s.ObjectName AND t.ObjectType=s.ObjectType)
WHEN MATCHED THEN
    UPDATE SET Status=@status,
               StartedAt = COALESCE(t.StartedAt, CASE WHEN @status IN ('InProgress','Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() END),
               EndedAt = CASE WHEN @status IN ('Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() ELSE NULL END,
               ObjectType=@objType,
               ErrorCode=@errCode,
               ErrorMessage=@errMsg
WHEN NOT MATCHED THEN
    INSERT (RunId, Stage, SchemaName, ObjectName, ObjectType, Status, StartedAt, EndedAt, ErrorCode, ErrorMessage)
    VALUES (@runId, @stage, @schema, @obj, @objType, @status,
            CASE WHEN @status IN ('InProgress','Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() ELSE NULL END,
            CASE WHEN @status IN ('Completed','Failed','Skipped') THEN SYSDATETIMEOFFSET() ELSE NULL END,
            @errCode, @errMsg);
";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@stage", stage);
        cmd.Parameters.AddWithValue("@schema", schemaName);
        cmd.Parameters.AddWithValue("@obj", objectName);
        cmd.Parameters.AddWithValue("@objType", objectType);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@errCode", (object?)errorCode ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@errMsg", (object?)errorMessage ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<HashSet<string>> GetCompletedStagesAsync(SqlConnection openSql, Guid runId, CancellationToken cancellationToken)
    {
        const string sql = "SELECT Stage FROM ToolMig.StageStatus WHERE RunId=@runId AND Status='Completed';";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@runId", runId);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
            set.Add(rdr.GetString(0));
        return set;
    }

    public async Task<HashSet<string>> GetCompletedObjectsAsync(SqlConnection openSql, Guid runId, string stage, CancellationToken cancellationToken)
    {
        const string sql = @"
SELECT SchemaName, ObjectName, ObjectType
FROM ToolMig.ObjectStatus
WHERE RunId=@runId AND Stage=@stage AND Status='Completed';
";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@stage", stage);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var rdr = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await rdr.ReadAsync(cancellationToken))
        {
            var schema = rdr.GetString(0);
            var obj = rdr.GetString(1);
            var typ = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2);
            // Backward compatible key (schema.obj) + type-aware key (schema.obj|TYPE)
            set.Add(schema + "." + obj);
            if (!string.IsNullOrWhiteSpace(typ)) set.Add(schema + "." + obj + "|" + typ);
        }
        return set;
    }

    public async Task MarkRunCompletedAsync(SqlConnection openSql, Guid runId, bool success, CancellationToken cancellationToken)
    {
        const string sql = @"
UPDATE ToolMig.Runs
SET EndedAt = SYSDATETIMEOFFSET(),
    Status = @status
WHERE RunId=@runId;
";
        await using var cmd = new SqlCommand(sql, openSql);
        cmd.Parameters.AddWithValue("@runId", runId);
        cmd.Parameters.AddWithValue("@status", success ? "Completed" : "Failed");
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
