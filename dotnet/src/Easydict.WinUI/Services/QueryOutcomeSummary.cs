namespace Easydict.WinUI.Services;

internal enum QueryExecutionOutcome
{
    Success,
    Neutral,
    Error,
    Cancelled
}

internal readonly record struct QueryOutcomeSummary(int SuccessCount, int ErrorCount)
{
    public static QueryOutcomeSummary From(IEnumerable<QueryExecutionOutcome> outcomes)
    {
        var successCount = 0;
        var errorCount = 0;

        foreach (var outcome in outcomes)
        {
            switch (outcome)
            {
                case QueryExecutionOutcome.Success:
                    successCount++;
                    break;
                case QueryExecutionOutcome.Error:
                    errorCount++;
                    break;
            }
        }

        return new QueryOutcomeSummary(successCount, errorCount);
    }
}
