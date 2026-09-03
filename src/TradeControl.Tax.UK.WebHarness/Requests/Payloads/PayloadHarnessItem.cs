namespace TradeControl.Tax.UK.WebHarness.Requests.Payloads;

public sealed class PayloadHarnessItem
{
    public required string Tag { get; init; }

    public required object Value { get; init; }
}
