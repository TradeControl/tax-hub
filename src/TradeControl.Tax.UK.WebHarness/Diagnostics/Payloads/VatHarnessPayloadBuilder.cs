using TradeControl.Tax.UK.WebHarness.Requests.Payloads;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Payloads;

public sealed class VatHarnessPayloadBuilder
{
    private readonly TcVatReader _reader;
    private readonly CategoryMapper _categoryMapper;

    public VatHarnessPayloadBuilder(TcVatReader reader, CategoryMapper categoryMapper)
    {
        _reader = reader;
        _categoryMapper = categoryMapper;
    }

    public async Task<VatHarnessPayload> BuildAsync(
        string connectionString,
        string taxSourceCode,
        string subjectCode,
        DateTime periodEndOn,
        CancellationToken cancellationToken = default)
    {
        var row = await _reader.ReadAsync(connectionString, periodEndOn, cancellationToken);
        if (row is null)
        {
            throw new InvalidOperationException("No VAT dataset row was found for the requested period.");
        }

        var items = new List<PayloadHarnessItem>
        {
            new() { Tag = "vatDueSales", Value = _categoryMapper.ToAmount(row.VatDueSales) },
            new() { Tag = "vatDueAcquisitions", Value = _categoryMapper.ToAmount(row.VatDueAcquisitions) },
            new() { Tag = "totalVatDue", Value = _categoryMapper.ToAmount(row.TotalVatDue) },
            new() { Tag = "vatReclaimedCurrPeriod", Value = _categoryMapper.ToAmount(row.VatReclaimedCurrPeriod) },
            new() { Tag = "netVatDue", Value = _categoryMapper.ToAmount(row.NetVatDue) },
            new() { Tag = "totalValueSalesExVAT", Value = _categoryMapper.ToWholeNumber(row.TotalValueSalesExVat) },
            new() { Tag = "totalValuePurchasesExVAT", Value = _categoryMapper.ToWholeNumber(row.TotalValuePurchasesExVat) },
            new() { Tag = "totalValueGoodsSuppliedExVAT", Value = _categoryMapper.ToWholeNumber(row.TotalValueGoodsSuppliedExVat) },
            new() { Tag = "totalValueGoodsReceivedExVAT", Value = _categoryMapper.ToWholeNumber(row.TotalValueGoodsReceivedExVat) }
        };

        return new VatHarnessPayload
        {
            PayloadVersion = "2026.1",
            TaxSourceCode = taxSourceCode,
            PeriodStart = row.StartOn.ToString("yyyy-MM-dd"),
            PeriodEnd = row.VatEndOn.ToString("yyyy-MM-dd"),
            SubjectCode = subjectCode,
            Items = items,
            Meta = new Dictionary<string, object?>
            {
                ["operation"] = "SUBMIT_VAT"
            }
        };
    }
}
