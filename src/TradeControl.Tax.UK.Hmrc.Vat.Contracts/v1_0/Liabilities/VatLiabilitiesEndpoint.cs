namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Liabilities;

public static class VatLiabilitiesEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/liabilities";
    public const string Method = "GET";
    public const string Version = "1.0";
    public const string Scope = "read:vat";
}
