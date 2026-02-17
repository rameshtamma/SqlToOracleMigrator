using Oracle.ManagedDataAccess.Client;

namespace SqlToOracleMigrator.Core.Oracle;

public enum UiSeverity
{
    Green,
    Yellow,
    Red
}

public sealed record UiErrorMessage(string Title, string Description, UiSeverity Severity, string[] Actions, int OracleNumber);

/// <summary>
/// v1.2: Catch OracleException by Number and surface custom UI messages with actionable steps.
/// Keep messages non-technical and consistent.
/// </summary>
public static class OracleErrorCatalog
{
    public static UiErrorMessage Map(OracleException ex)
    {
        if (ex is null) throw new ArgumentNullException(nameof(ex));

        return ex.Number switch
        {
            955 => new UiErrorMessage(
                "Schema conflict — object already exists",
                "Oracle reports the object name already exists. This usually happens when the target schema is not clean or the object was created in a prior run.",
                UiSeverity.Yellow,
                new[] { "Retry", "Smart Reset", "Skip" },
                ex.Number),

            942 => new UiErrorMessage(
                "Missing object or insufficient access",
                "Oracle could not find an object referenced by the script, or the connected user lacks access to required system views.",
                UiSeverity.Yellow,
                new[] { "Grant Privileges", "Retry" },
                ex.Number),

            904 => new UiErrorMessage(
                "Invalid identifier in generated DDL",
                "A column or identifier name is not valid for Oracle. The tool can auto-quote reserved words in later stages, but this item needs review.",
                UiSeverity.Red,
                new[] { "Fix Script", "Retry" },
                ex.Number),

            910 => new UiErrorMessage(
                "Identifier too long for Oracle",
                "Oracle has strict identifier length limits. The tool will truncate and alias names, but this item needs a manual mapping decision.",
                UiSeverity.Red,
                new[] { "Fix Script", "Retry" },
                ex.Number),

            1031 => new UiErrorMessage(
                "Insufficient privileges",
                "The Oracle user does not have privileges required to create schema objects.",
                UiSeverity.Red,
                new[] { "Grant Privileges", "Retry" },
                ex.Number),

            1940 => new UiErrorMessage(
                "User is currently connected",
                "Oracle cannot drop a user while there are active sessions for that user. Disconnect active sessions and retry.",
                UiSeverity.Yellow,
                new[] { "Disconnect Sessions", "Retry" },
                ex.Number),

            1950 => new UiErrorMessage(
                "No tablespace quota",
                "The schema user has no quota on the default tablespace. Grant UNLIMITED TABLESPACE or a quota on the chosen tablespace.",
                UiSeverity.Red,
                new[] { "Grant Quota", "Retry" },
                ex.Number),

            1400 => new UiErrorMessage(
                "Missing required data",
                "Oracle rejected a NULL value for a required (NOT NULL) column. This can happen when source data violates constraints or when staging columns are used.",
                UiSeverity.Red,
                new[] { "Review Source Data", "Apply Default Policy", "Retry" },
                ex.Number),

            1843 => new UiErrorMessage(
                "Invalid date value",
                "Oracle could not interpret a date/time value. Normalize date formats or clamp invalid values and retry.",
                UiSeverity.Yellow,
                new[] { "Normalize Dates", "Retry" },
                ex.Number),

            44003 => new UiErrorMessage(
                "Bulk load name validation failed",
                "OracleBulkCopy rejected the destination table name. The tool will automatically retry using a safe fallback load method.",
                UiSeverity.Yellow,
                new[] { "Retry", "Use Fallback Loader" },
                ex.Number),

            50000 => new UiErrorMessage(
                "Operation timed out",
                "Oracle signaled a timeout. Reduce parallelism or batch size and retry.",
                UiSeverity.Yellow,
                new[] { "Reduce DOP", "Retry" },
                ex.Number),

            29532 => new UiErrorMessage(
                "Spatial conversion failed",
                "A spatial conversion failed during staging or post-load conversion. The tool can keep staging columns for manual remediation.",
                UiSeverity.Yellow,
                new[] { "Keep Staging Columns", "Retry" },
                ex.Number),

            _ => new UiErrorMessage(
                "Oracle error",
                ex.Message,
                UiSeverity.Red,
                new[] { "Retry", "Manual Fix" },
                ex.Number)
        };
    }
}
