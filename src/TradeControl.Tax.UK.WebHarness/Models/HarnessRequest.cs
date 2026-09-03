namespace TradeControl.Tax.UK.WebHarness.Requests;

public sealed class HarnessRequest
{
    public required string ConnectionString { get; init; }

    public required string TenantId { get; init; }

    public required string SubjectId { get; init; }

    public required string Period { get; init; }

    public required string TaxSourceCode { get; init; }

    public string Environment { get; init; } = "sandbox";
}
