namespace TradeControl.Tax.Data;

public sealed record SelfEmploymentFilingContext(
    string Nino,
    BusinessId BusinessId,
    string AccountingBasis,
    string QuarterlyPeriodType,
    IReadOnlyList<FactProvenance> Provenance);

public sealed record SelfEmploymentFilingContextSelector(
    SourceKey Source,
    string SubjectCode,
    TaxSourceCode TaxSourceCode,
    DateOnly AsOfDate);

public interface ISelfEmploymentFilingContextReader
{
    Task<SelfEmploymentFilingContext> ReadAsync(
        SelfEmploymentFilingContextSelector selector,
        CancellationToken cancellationToken = default);
}
