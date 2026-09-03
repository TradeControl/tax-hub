using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Obligations;

public class SaObligationsRequest
{
    [JsonIgnore]
    public string Utr { get; set; }

    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    public SaObligationsRequest(string utr, DateOnly? from = null, DateOnly? to = null)
    {
        Utr = utr;
        From = from;
        To = to;
    }

    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions { WriteIndented = indented };
        return JsonSerializer.Serialize(this, options);
    }
}
