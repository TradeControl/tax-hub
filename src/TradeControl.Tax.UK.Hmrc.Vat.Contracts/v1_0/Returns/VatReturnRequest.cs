using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

public class VatReturnRequest
{
    [JsonIgnore]
    public string Vrn { get; set; } = string.Empty;   // path
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

    public string ToJson(bool indented = false) => global::TradeControl.Tax.UK.Hmrc.Vat.VatJson.Serialize(this, indented);
}
