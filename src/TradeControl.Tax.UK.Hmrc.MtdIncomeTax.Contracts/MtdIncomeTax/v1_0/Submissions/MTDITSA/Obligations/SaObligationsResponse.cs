using System.Text.Json;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Obligations
{
    public class SaObligationsResponse
    {
        public List<SaObligation> Obligations { get; set; } = new();

        public SaObligationsResponse(JsonElement json)
        {
            if (json.TryGetProperty("obligations", out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                    Obligations.Add(new SaObligation(item));
            }
        }
    }
}
