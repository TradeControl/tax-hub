using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Liabilities;

public class VatLiabilitiesResponse
{
    public List<VatLiability> Liabilities { get; set; } = new();

    public VatLiabilitiesResponse() { }

    public VatLiabilitiesResponse(string json)
    {
        var obj = JsonSerializer.Deserialize<VatLiabilitiesResponse>(json);
        if (obj != null)
        {
            Liabilities = obj.Liabilities;
        }
    }

    public static VatLiabilitiesResponse FromJson(string json)
    {
        return new VatLiabilitiesResponse(json);
    }
}
