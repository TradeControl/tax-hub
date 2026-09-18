namespace TradeControl.Tax.Data;

public sealed record VatReturnSelector(SourceKey Source, TaxReportingPeriod Period);

public sealed record BusinessIncomeSelector(
    SourceKey Source,
    TaxSourceCode TaxSourceCode,
    BusinessId BusinessId,
    TaxReportingPeriod Period);

public sealed record SourceReadinessRequest(
    SourceKey Source,
    TaxSubject Subject,
    TaxReportingPeriod Period,
    TaxSourceCode? TaxSourceCode = null);

public interface IVatReturnSourceReader
{
    Task<VatReturnSource> ReadAsync(VatReturnSelector selector, CancellationToken cancellationToken = default);
}

public interface IBusinessIncomeSourceReader
{
    Task<BusinessIncomeSource> ReadAsync(BusinessIncomeSelector selector, CancellationToken cancellationToken = default);
}

public interface ISourceReadinessEvaluator
{
    Task<SourceReadiness> EvaluateAsync(SourceReadinessRequest request, CancellationToken cancellationToken = default);
}
