using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

public class VatReturnResponse
{
    public DateTime ProcessingDate { get; set; }
    public string? PaymentIndicator { get; set; }
    public string? FormBundleNumber { get; set; }
    public string? ChargeRefNumber { get; set; }

    public VatReturnResponse() { }

    public VatReturnResponse(string json)
    {
        var obj = JsonSerializer.Deserialize<VatReturnResponse>(json);
        if (obj != null)
        {
            ProcessingDate = obj.ProcessingDate;
            PaymentIndicator = obj.PaymentIndicator;
            FormBundleNumber = obj.FormBundleNumber;
            ChargeRefNumber = obj.ChargeRefNumber;
        }
    }

    public static VatReturnResponse FromJson(string json)
    {
        return new VatReturnResponse(json);
    }
}
