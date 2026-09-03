namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Obligations;

public static class VatObligationsEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/obligations";
    public const string Method = "GET";
    public const string Version = "1.0";
    public const string Scope = "read:vat";
}
