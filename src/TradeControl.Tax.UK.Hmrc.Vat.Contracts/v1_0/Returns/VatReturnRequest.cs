using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

public class VatReturnRequest
{
    [JsonIgnore]
    public required string Vrn { get; set; }          // path
    public required string PeriodKey { get; set; }

    public decimal VatDueSales { get; set; }
    public decimal VatDueAcquisitions { get; set; }
    public decimal TotalVatDue { get; set; }
    public decimal VatReclaimedCurrPeriod { get; set; }
    public decimal NetVatDue { get; set; }

    public decimal TotalValueSalesExVat { get; set; }
    public decimal TotalValuePurchasesExVat { get; set; }
    public decimal TotalValueGoodsSuppliedExVat { get; set; }
    public decimal TotalAcquisitionsExVat { get; set; }

    public bool Finalised { get; set; }

    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented
        };

        return JsonSerializer.Serialize(this, options);
    }
}
