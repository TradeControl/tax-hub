using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

public class VatFinancialDetailsRequest
{
    [JsonIgnore]
    public required string Vrn { get; set; }
    public string? PenaltyChargeReference { get; set; }

    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented
        };

        return JsonSerializer.Serialize(this, options);
    }
}
