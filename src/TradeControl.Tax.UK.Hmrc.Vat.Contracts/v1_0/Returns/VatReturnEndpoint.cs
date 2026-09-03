namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

public static class VatReturnEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/returns";
    public const string Method = "POST";
    public const string Version = "1.0";
    public const string Scope = "write:vat";
}
