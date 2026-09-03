namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Payments;

public static class VatPaymentsEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/payments";
    public const string Method = "GET";
    public const string Version = "1.0";
    public const string Scope = "read:vat";
}
