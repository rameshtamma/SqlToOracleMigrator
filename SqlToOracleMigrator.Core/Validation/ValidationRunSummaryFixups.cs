
using SqlToOracleMigrator.Core.Reporting;

namespace SqlToOracleMigrator.Core.Validation
{
    internal static class ValidationRunSummaryFixups
    {
        internal static void EnsureStageTablePresent(string runDir)
        {
            RunSummaryHtmlBuilder.Rebuild(runDir);
        }
    }
}
