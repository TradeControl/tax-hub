using TradeControl.Tax.UK.WebHarness.Requests.Payloads;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.Data;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Payloads;

public sealed class VatHarnessPayloadBuilder
{
    private readonly ConnectionFactory _connections;
    private readonly CategoryMapper _categoryMapper;

    public VatHarnessPayloadBuilder(ConnectionFactory connections, CategoryMapper categoryMapper)
    {
        _connections = connections;
        _categoryMapper = categoryMapper;
    }

    public async Task<VatHarnessPayload> BuildAsync(
        string connectionString,
        string taxSourceCode,
        string subjectCode,
        DateTime periodEndOn,
        CancellationToken cancellationToken = default)
    {
        var key = new SourceKey("harness-request");
        var reader = new TradeControlTaxSourceAdapter(_connections,
            new SourceConnectionResolver([new(key, connectionString)]));
        var end = DateOnly.FromDateTime(periodEndOn);
        var period = new TaxReportingPeriod(new(end.Year, end.Month, 1), end, TaxPeriodKind.Vat,
            $"VAT-{end:yyyy-MM-dd}");
        var source = await reader.ReadAsync(new VatReturnSelector(key, period), cancellationToken);
        static decimal Amount(TaxFact<decimal> fact) => fact.Value.HasValue
            ? fact.Value.Value : throw new InvalidOperationException($"VAT fact '{fact.StableKey}' has no usable value.");

        var items = new List<PayloadHarnessItem>
        {
            new() { Tag = "vatDueSales", Value = _categoryMapper.ToAmount(Amount(source.VatDueSales)) },
            new() { Tag = "vatDueAcquisitions", Value = _categoryMapper.ToAmount(Amount(source.VatDueAcquisitions)) },
            new() { Tag = "totalVatDue", Value = _categoryMapper.ToAmount(Amount(source.TotalVatDue)) },
            new() { Tag = "vatReclaimedCurrPeriod", Value = _categoryMapper.ToMagnitudeAmount(Amount(source.VatReclaimedCurrentPeriod)) },
            new() { Tag = "netVatDue", Value = _categoryMapper.ToAmount(Amount(source.NetVatDue)) },
            new() { Tag = "totalValueSalesExVAT", Value = _categoryMapper.ToWholeNumber(Amount(source.TotalValueSalesExVat)) },
            new() { Tag = "totalValuePurchasesExVAT", Value = _categoryMapper.ToWholeNumber(Amount(source.TotalValuePurchasesExVat)) },
            new() { Tag = "totalValueGoodsSuppliedExVAT", Value = _categoryMapper.ToWholeNumber(Amount(source.TotalValueGoodsSuppliedExVat)) },
            new() { Tag = "totalValueGoodsReceivedExVAT", Value = _categoryMapper.ToWholeNumber(Amount(source.TotalAcquisitionsExVat)) }
        };

        return new VatHarnessPayload
        {
            PayloadVersion = "2026.1",
            TaxSourceCode = taxSourceCode,
            PeriodStart = source.Period.Start.ToString("yyyy-MM-dd"),
            PeriodEnd = source.Period.End.ToString("yyyy-MM-dd"),
            SubjectCode = subjectCode,
            Items = items,
            Meta = new Dictionary<string, object?>
            {
                ["operation"] = "SUBMIT_VAT"
            }
        };
    }
}
