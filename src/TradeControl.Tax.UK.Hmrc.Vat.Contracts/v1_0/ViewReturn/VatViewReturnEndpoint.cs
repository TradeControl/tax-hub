namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.ViewReturn;

public static class VatViewReturnEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/returns/{periodKey}";
    public const string Method = "GET";
    public const string Version = "1.0";
    public const string Scope = "read:vat";
}
