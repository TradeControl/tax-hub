using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Payments;

public class VatPaymentsRequest
{
    [JsonIgnore]
    public required string Vrn { get; set; }
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }

    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented
        };

        return JsonSerializer.Serialize(this, options);
    }
}
