namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TcSubmissionHistory
{
    public string SubmissionReference { get; init; } = string.Empty;

    public DateTimeOffset SubmittedAt { get; init; }
}
