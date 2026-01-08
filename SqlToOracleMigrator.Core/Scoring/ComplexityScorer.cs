namespace SqlToOracleMigrator.Core.Scoring;

/// <summary>
/// Heuristic 1-5 complexity score used for reporting/UI prioritization.
/// This is NOT a guarantee of conversion difficulty; it is a lightweight signal.
/// </summary>
public static class ComplexityScorer
{
    public static int Score(string objectType, long? estimatedRows, double? estimatedSizeMb, int? dependsOn, int? dependedBy)
    {
        var t = (objectType ?? string.Empty).Trim().ToLowerInvariant();
        var score = t switch
        {
            "procedure" => 4,
            "function" => 4,
            "view" => 3,
            "table" => 2,
            "trigger" => 3,
            _ => 1
        };

        var dep = (dependsOn ?? 0) + (dependedBy ?? 0);
        if (dep >= 50) score += 2;
        else if (dep >= 20) score += 1;

        if (estimatedRows is long r)
        {
            if (r >= 50_000_000) score += 2;
            else if (r >= 5_000_000) score += 1;
        }

        if (estimatedSizeMb is double mb)
        {
            if (mb >= 5_000) score += 2;     // ~5GB+
            else if (mb >= 1_000) score += 1; // ~1GB+
        }

        // Clamp to 1..5
        if (score < 1) score = 1;
        if (score > 5) score = 5;
        return score;
    }
}
