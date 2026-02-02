namespace SqlToOracleMigrator.Core;

/// <summary>
/// Computes a lightweight "complexity" indicator for UI display and triage.
/// The score is intentionally heuristic (1..10) and does not affect migration logic.
/// </summary>
public static class ComplexityScorer
{
    public static int Score(
        string objectType,
        long? estimatedRows,
        double? estimatedSizeMb,
        int? dependsOnCount,
        int? dependedByCount)
    {
        var t = (objectType ?? string.Empty).Trim().ToUpperInvariant();
        var score = 1;

        // Type baseline
        score += t switch
        {
            "TABLE" => 2,
            "VIEW" => 2,
            "MATERIALIZED VIEW" => 3,
            "INDEX" => 2,
            "SEQUENCE" => 1,
            "SYNONYM" => 1,
            "TRIGGER" => 3,
            "PROCEDURE" => 4,
            "FUNCTION" => 4,
            "PACKAGE" => 5,
            "PACKAGE BODY" => 6,
            "TYPE" => 4,
            "TYPE BODY" => 5,
            _ => 2
        };

        // Size / row influence (log-ish via thresholds)
        if (estimatedRows is > 10_000_000) score += 3;
        else if (estimatedRows is > 1_000_000) score += 2;
        else if (estimatedRows is > 100_000) score += 1;

        if (estimatedSizeMb is > 10_000) score += 3;
        else if (estimatedSizeMb is > 1_000) score += 2;
        else if (estimatedSizeMb is > 200) score += 1;

        // Dependency influence
        var dep = (dependsOnCount ?? 0) + (dependedByCount ?? 0);
        if (dep > 50) score += 3;
        else if (dep > 20) score += 2;
        else if (dep > 5) score += 1;

        // Clamp to a small UI-friendly range
        if (score < 1) score = 1;
        if (score > 10) score = 10;
        return score;
    }
}
