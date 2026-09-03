using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.CustomerInformation;

public class VatCustomerInformationResponse
{
    public string? Name { get; set; }
    public string? TradingName { get; set; }
    public string? AddressLine1 { get; set; }
    public string? AddressLine2 { get; set; }
    public string? AddressLine3 { get; set; }
    public string? Postcode { get; set; }
    public bool FlatRateScheme { get; set; }
    public decimal? FlatRatePercentage { get; set; }

    public VatCustomerInformationResponse() { }

    public VatCustomerInformationResponse(string json)
    {
        var obj = JsonSerializer.Deserialize<VatCustomerInformationResponse>(json);
        if (obj != null)
        {
            Name = obj.Name;
            TradingName = obj.TradingName;
            AddressLine1 = obj.AddressLine1;
            AddressLine2 = obj.AddressLine2;
            AddressLine3 = obj.AddressLine3;
            Postcode = obj.Postcode;
            FlatRateScheme = obj.FlatRateScheme;
            FlatRatePercentage = obj.FlatRatePercentage;
        }
    }

    public static VatCustomerInformationResponse FromJson(string json)
    {
        return new VatCustomerInformationResponse(json);
    }
}
