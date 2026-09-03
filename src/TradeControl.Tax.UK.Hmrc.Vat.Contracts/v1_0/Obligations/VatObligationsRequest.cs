using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Obligations;

public class VatObligationsRequest
{
    [JsonIgnore]
    public required string Vrn { get; set; }            // path
    public DateTime? From { get; set; }                 // query
    public DateTime? To { get; set; }                   // query
    public required string Status { get; set; }         // "O" or "F"

    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented
        };

        return JsonSerializer.Serialize(this, options);
    }
}
