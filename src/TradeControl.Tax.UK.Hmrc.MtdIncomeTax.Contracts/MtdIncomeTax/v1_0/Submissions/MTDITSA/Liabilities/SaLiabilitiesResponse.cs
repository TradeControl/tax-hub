using System.Text.Json;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Liabilities;

public class SaLiabilitiesResponse
{
    public SaBalanceDetails BalanceDetails { get; set; }
    public List<SaChargeDetail> ChargeDetails { get; set; } = new();

    public SaLiabilitiesResponse(JsonElement json)
    {
        BalanceDetails = json.TryGetProperty("balanceDetails", out var bal)
            ? new SaBalanceDetails(bal)
            : new SaBalanceDetails(default);

        if (json.TryGetProperty("chargeDetails", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
                ChargeDetails.Add(new SaChargeDetail(item));
        }
    }
}
