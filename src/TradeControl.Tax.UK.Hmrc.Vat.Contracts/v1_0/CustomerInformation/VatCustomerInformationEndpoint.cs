namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.CustomerInformation;

public static class VatCustomerInformationEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/information";
    public const string Method = "GET";
    public const string Version = "1.0";
    public const string Scope = "read:vat";
}
