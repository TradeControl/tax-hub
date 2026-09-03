using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Penalties;

public class VatPenaltiesResponse
{
    public List<VatPenalty> Penalties { get; set; } = new();

    public VatPenaltiesResponse() { }

    public VatPenaltiesResponse(string json)
    {
        var obj = JsonSerializer.Deserialize<VatPenaltiesResponse>(json);
        if (obj != null)
        {
            Penalties = obj.Penalties;
        }
    }

    public static VatPenaltiesResponse FromJson(string json)
    {
        return new VatPenaltiesResponse(json);
    }
}
