using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.ViewReturn;

public class VatViewReturnResponse
{
    public string? PeriodKey { get; set; }
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

    public VatViewReturnResponse() { }

    public VatViewReturnResponse(string json)
    {
        var obj = JsonSerializer.Deserialize<VatViewReturnResponse>(json);
        if (obj != null)
        {
            PeriodKey = obj.PeriodKey;
            VatDueSales = obj.VatDueSales;
            VatDueAcquisitions = obj.VatDueAcquisitions;
            TotalVatDue = obj.TotalVatDue;
            VatReclaimedCurrPeriod = obj.VatReclaimedCurrPeriod;
            NetVatDue = obj.NetVatDue;
            TotalValueSalesExVat = obj.TotalValueSalesExVat;
            TotalValuePurchasesExVat = obj.TotalValuePurchasesExVat;
            TotalValueGoodsSuppliedExVat = obj.TotalValueGoodsSuppliedExVat;
            TotalAcquisitionsExVat = obj.TotalAcquisitionsExVat;
            Finalised = obj.Finalised;
        }
    }

    public static VatViewReturnResponse FromJson(string json)
    {
        return new VatViewReturnResponse(json);
    }
}
