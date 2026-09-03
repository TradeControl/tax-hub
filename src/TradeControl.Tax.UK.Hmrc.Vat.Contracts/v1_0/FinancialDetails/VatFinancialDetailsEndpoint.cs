namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

public static class VatFinancialDetailsEndpoint
{
    public const string Path = "/organisations/vat/{vrn}/financial-details/{penaltyChargeReference}";
    public const string Method = "GET";
    public const string Version = "1.0";
    public const string Scope = "read:vat";
}
