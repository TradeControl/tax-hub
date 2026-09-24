using System.Text;
using System.Text.Json;
using System.Reflection;
using TradeControl.Tax.UK.Hmrc.Vat;
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

Assert(VatOperationCatalog.All.Count == 8, "Every VAT endpoint must appear exactly once in the operation catalogue.");
Assert(VatOperationCatalog.All.Select(x => x.OperationId).Distinct(StringComparer.Ordinal).Count() == 8,
    "VAT operation IDs must be unique.");
var reflectedVatEndpoints = typeof(VatReturnEndpoint).Assembly.GetTypes()
    .Where(type => type.IsAbstract && type.IsSealed && type.Name.EndsWith("Endpoint", StringComparison.Ordinal))
    .Select(type => new
    {
        Method = Convert.ToString(type.GetField("Method", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue()),
        Path = Convert.ToString(type.GetField("Path", BindingFlags.Public | BindingFlags.Static)?.GetRawConstantValue())
    }).Where(x => x.Method is not null && x.Path is not null).ToArray();
Assert(reflectedVatEndpoints.Length == VatOperationCatalog.All.Count
    && reflectedVatEndpoints.All(x => VatOperationCatalog.All.Count(y => y.Method == x.Method && y.PathTemplate == x.Path) == 1),
    "A VAT endpoint was added, removed or left unclassified.");
var submit = VatOperationCatalog.All.Single(x => x.OperationId == "vat.returns.submit");
Assert(submit.Shape == VatRequestShape.AccountingBody && submit.ContentType == "application/json"
    && submit.OAuthScope == "write:vat" && submit.SuccessStatusCode == 201
    && submit.ResponseType == typeof(VatReturnResponse)
    && submit.HasContractFixture && submit.HasPopulationFixture && submit.HasHarnessCoverage,
    "VAT return coverage metadata is incomplete.");
var described = VatOperationCatalog.All.Where(x => x.Shape == VatRequestShape.BodylessEnquiry
    && x.AccountsMode == VatAccountsModeDecision.Supported).ToArray();
Assert(described.Select(x => x.OperationId).SequenceEqual(["vat.obligations.list", "vat.returns.retrieve"])
    && described.All(x => x.OAuthScope == "read:vat" && x.SuccessStatusCode == 200 && x.ResponseType is not null)
    && described.All(x => x.HasContractFixture && !x.HasPopulationFixture && x.HasHarnessCoverage),
    "Only the approved VAT obligations and view-return descriptions may advertise Phase 8 coverage.");

var bytes = VatJson.SerializeCanonical(request);
var bytesAgain = VatJson.SerializeCanonical(request);
var json = Encoding.UTF8.GetString(bytes);
Assert(bytes.SequenceEqual(bytesAgain) && (bytes.Length < 3 || !bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF })),
    "VAT canonical JSON is not deterministic BOM-free UTF-8.");
Assert(json == "{\"periodKey\":\"24A1\",\"vatDueSales\":0,\"vatDueAcquisitions\":1.25,\"totalVatDue\":1.25,\"vatReclaimedCurrPeriod\":0,\"netVatDue\":1.25,\"totalValueSalesExVat\":0,\"totalValuePurchasesExVat\":1,\"totalValueGoodsSuppliedExVat\":0,\"totalAcquisitionsExVat\":0,\"finalised\":true}",
    "VAT canonical request bytes changed.");
using (var document = JsonDocument.Parse(bytes))
{
    Assert(!document.RootElement.TryGetProperty("vrn", out _), "VAT path parameters entered the request body.");
    Assert(document.RootElement.GetProperty("vatDueSales").GetDecimal() == 0m, "An explicit VAT zero was omitted.");
    Assert(document.RootElement.GetProperty("finalised").GetBoolean(), "The VAT finalised flag changed.");
}
request.Finalised = false;
using (var document = JsonDocument.Parse(VatJson.SerializeCanonical(request)))
    Assert(document.RootElement.TryGetProperty("finalised", out var finalised) && !finalised.GetBoolean(),
        "An explicit VAT false value was omitted.");

Console.WriteLine($"VAT contract tests passed ({assertions} assertions).");

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
