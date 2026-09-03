namespace TradeControl.Tax.UK.Adapters.Submission.Audit;

public sealed class SubmissionLogger
{
    public Task LogAsync(string operationType, string status, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
