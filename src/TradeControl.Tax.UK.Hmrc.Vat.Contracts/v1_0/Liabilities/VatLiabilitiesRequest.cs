using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Liabilities;

public class VatLiabilitiesRequest
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
