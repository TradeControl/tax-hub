using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.CustomerInformation;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

public class VatFinancialDetailsResponse
{
    public List<VatFinancialDetail> FinancialDetails { get; set; } = new();

    public VatFinancialDetailsResponse () { }

    public VatFinancialDetailsResponse(string json)
    {
        var obj = JsonSerializer.Deserialize<VatFinancialDetailsResponse>(json);
        if (obj != null)
        {
            FinancialDetails = obj.FinancialDetails;
        }
    }

    public static VatFinancialDetailsResponse FromJson(string json)
    {
        return new VatFinancialDetailsResponse(json);
    }
}
