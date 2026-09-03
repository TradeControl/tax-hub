using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.ViewReturn;

public class VatViewReturnRequest
{
    [JsonIgnore]
    public required string Vrn { get; set; }          // path
    public required string PeriodKey { get; set; }

    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented
        };

        return JsonSerializer.Serialize(this, options);
    }
}
