using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Obligations;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;

var assertions = 0;

Assert(VatReturnEndpoint.Method == "POST", "VAT return endpoint method changed.");
Assert(VatReturnEndpoint.Path == "/organisations/vat/{vrn}/returns", "VAT return endpoint path changed.");
Assert(VatReturnEndpoint.Scope == "write:vat", "VAT return endpoint scope changed.");
Assert(VatObligationsEndpoint.Method == "GET", "VAT obligations endpoint method changed.");

var request = new VatReturnRequest
{
    Vrn = "123456789",
    PeriodKey = "24A1",
    VatDueSales = 0m,
    VatDueAcquisitions = 1.25m,
    TotalVatDue = 1.25m,
    VatReclaimedCurrPeriod = 0m,
    NetVatDue = 1.25m,
    TotalValueSalesExVat = 0,
    TotalValuePurchasesExVat = 1,
    TotalValueGoodsSuppliedExVat = 0,
    TotalAcquisitionsExVat = 0,
    Finalised = true
};

Assert(request.Vrn == "123456789", "VAT path parameter value changed.");
Assert(request.VatDueSales == 0m, "Explicit VAT zero was not preserved by the contract object.");
Assert(request.Finalised, "VAT finalised value changed.");

Console.WriteLine($"VAT contract tests passed ({assertions} assertions).");

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
