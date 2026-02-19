
using SqlToOracleMigrator.Core.Reporting;

namespace SqlToOracleMigrator.Core
{
    public sealed partial class MigrationEngine
    {
        /// <summary>
        /// Ensures RunSummary.html contains the full stage execution table and preserved Post-migration validation section.
        /// Safe to call multiple times.
        /// </summary>
        internal void RebuildRunSummaryHtml(string runDir) => RunSummaryHtmlBuilder.Rebuild(runDir);
    }
}
