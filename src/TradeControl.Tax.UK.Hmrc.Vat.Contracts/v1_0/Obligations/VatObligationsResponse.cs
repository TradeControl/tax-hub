using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Obligations;

public class VatObligationsResponse
{
    public List<VatObligation> Obligations { get; set; } = new();
    public VatObligationsResponse() { }

    public VatObligationsResponse(string json)
    {
        var obj = JsonSerializer.Deserialize<VatObligationsResponse>(json);
        if (obj != null)
        {
            Obligations = obj.Obligations;
        }
    }

    public static VatObligationsResponse FromJson(string json)
    {
        return new VatObligationsResponse(json);
    }
}
