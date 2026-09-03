using System.Text.Json;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Payments;

public class SaPaymentsResponse
{
    public List<SaPayment> Payments { get; set; } = new();

    public SaPaymentsResponse(JsonElement json)
    {
        if (json.TryGetProperty("payments", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
                Payments.Add(new SaPayment(item));
        }
    }
}
