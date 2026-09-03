using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Payments;

public class SaPayment
{
    public decimal Amount { get; set; }
    public DateOnly Received { get; set; }
    public string? Method { get; set; }
    public List<string> AllocatedTo { get; set; } = new();

    public SaPayment(JsonElement json)
    {
        Amount = JsonExtract.GetDecimal(json, "amount");
        Received = JsonExtract.GetDateOnly(json, "received");
        Method = JsonExtract.GetString(json, "method");

        if (json.TryGetProperty("allocatedTo", out var arr))
        {
            foreach (var item in arr.EnumerateArray())
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s))
                    AllocatedTo.Add(s);
            }
        }
    }
}
