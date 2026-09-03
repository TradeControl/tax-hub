namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Penalties;

public static class VatPenaltiesEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/penalties";
    public const string Method = "GET";
    public const string Version = "1.0";
    public const string Scope = "read:vat";
}
