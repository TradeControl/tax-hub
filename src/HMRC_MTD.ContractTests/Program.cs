using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Accounts.V4;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessAdjustments.V7;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessDetails.V2;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessIncomeSummary.V3;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Finalisation.V8;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Losses.V6;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Losses.V7;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Obligations.V3;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.SelfEmployment.V5.Annual;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.SelfEmployment.V5.Cumulative;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.TaxLiabilityAdjustments.V1;
using CalculationResponse2025 = TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8.Wire2025.CalculationResponse2025;
using CalculationResponse2026 = TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8.Wire2026.CalculationResponse2026;
using BissWireResponse = TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessIncomeSummary.V3.Wire.BissWireResponse;
using BusinessListWireResponse = TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessDetails.V2.Wire.list.BusinessListWireResponse;
using BusinessDetailWireResponse = TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessDetails.V2.Wire.detail.BusinessDetailWireResponse;
using LadrElectionWireResponse = TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessDetails.V2.Wire.ladr.LadrElectionWireResponse;

var options = SaJson.CreateOptions();
var checks = 0;
void Assert(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}

static HashSet<string> JsonPaths(string json)
{
    using var document = JsonDocument.Parse(json);
    var paths = new HashSet<string>(StringComparer.Ordinal);
    void Visit(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                var next = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                paths.Add(next);
                Visit(property.Value, next);
            }
        else if (element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0)
            Visit(element[0], path + "[]");
    }
    Visit(document.RootElement, string.Empty);
    return paths;
}

void AssertCompleteRoundTrip<T>(string fixtureName)
{
    var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName));
    var model = JsonSerializer.Deserialize<T>(source, options);
    Assert(model is not null, $"{fixtureName} did not deserialize.");
    var roundTrip = JsonSerializer.Serialize(model, options);
    var missing = JsonPaths(source).Except(JsonPaths(roundTrip)).ToList();
    Assert(missing.Count == 0, $"{fixtureName} contains unmodelled wire fields: {string.Join(", ", missing)}");
}

var endpoints = BusinessDetailsEndpoints.All
    .Concat(ObligationEndpoints.All)
    .Concat(CumulativeEndpoints.All)
    .Concat(AnnualEndpoints.Production)
    .Concat(BusinessAdjustmentEndpoints.All)
    .Concat(BusinessIncomeSummaryEndpoints.All)
    .Concat(LossV6Endpoints.All)
    .Concat(LossV7Endpoints.All)
    .Concat(TaxLiabilityAdjustmentEndpoints.All)
    .Concat(CalculationEndpoints.All)
    .Concat(FinalisationEndpoints.All)
    .Concat(AccountEndpoints.All)
    .ToList();

Assert(endpoints.Count >= 38, "The supported endpoint inventory is incomplete.");
Assert(endpoints.All(x => x.Method is "GET" or "PUT" or "POST" or "DELETE"), "Every endpoint must constrain its HTTP method.");
Assert(endpoints.All(x => x.PathTemplate.StartsWith('/')), "Every endpoint must have an absolute path template.");
Assert(endpoints.All(x => x.Accept == $"application/vnd.hmrc.{x.ApiVersion}+json"), "Every endpoint must expose matching HMRC media/API version metadata.");
Assert(endpoints.Where(x => x.Method is "PUT" || (x.Method == "POST" && x.HasRequestBody)).All(x => x.ContentType == "application/json"), "JSON writes must declare their content type.");
Assert(endpoints.Where(x => x.Method == "GET").All(x => !x.HasRequestBody), "GET query models must never be request bodies.");
Assert(CumulativeEndpoints.Put.PathTemplate == "/individuals/business/self-employment/{nino}/{businessId}/cumulative/{taxYear}" && CumulativeEndpoints.Put.Method == "PUT", "Cumulative PUT metadata is incorrect.");
Assert(CumulativeEndpoints.Get.Method == "GET" && CumulativeEndpoints.Get.SuccessStatusCode == 200, "Cumulative GET metadata is incorrect.");
Assert(CumulativeEndpoints.Put.SuccessStatusCode == 204, "Cumulative PUT must return 204.");
Assert(FinalisationEndpoints.FinalDeclaration.PathTemplate.EndsWith("/{calculationId}/final-declaration") && !FinalisationEndpoints.FinalDeclaration.HasRequestBody && FinalisationEndpoints.FinalDeclaration.SuccessStatusCode == 204, "Final declaration must be a bodyless 204 POST.");
Assert(CalculationEndpoints.Trigger.SuccessStatusCode == 202 && !CalculationEndpoints.Trigger.HasRequestBody, "Calculation trigger must be a bodyless 202 POST.");

var detailed = new CumulativeSubmission
{
    PeriodDates = new() { PeriodStartDate = new DateOnly(2025, 4, 6), PeriodEndDate = new DateOnly(2025, 7, 5) },
    PeriodIncome = new() { Turnover = 0m, Other = 2m, TaxTakenOffTradingIncome = 3m },
    PeriodExpenses = new DetailedPeriodExpenses
    {
        CostOfGoods = -1m, PaymentsToSubcontractors = 2m, WagesAndStaffCosts = 3m,
        CarVanTravelExpenses = 4m, PremisesRunningCosts = 5m, MaintenanceCosts = 6m,
        AdminCosts = 7m, BusinessEntertainmentCosts = 8m, AdvertisingCosts = 9m,
        InterestOnBankOtherLoans = 10m, FinanceCharges = 11m, IrrecoverableDebts = 12m,
        ProfessionalFees = 13m, Depreciation = 14m, OtherExpenses = 15m
    },
    PeriodDisallowableExpenses = new()
    {
        CostOfGoodsDisallowable = 1m, PaymentsToSubcontractorsDisallowable = 2m,
        WagesAndStaffCostsDisallowable = 3m, CarVanTravelExpensesDisallowable = 4m,
        PremisesRunningCostsDisallowable = 5m, MaintenanceCostsDisallowable = 6m,
        AdminCostsDisallowable = 7m, BusinessEntertainmentCostsDisallowable = 8m,
        AdvertisingCostsDisallowable = 9m, InterestOnBankOtherLoansDisallowable = 10m,
        FinanceChargesDisallowable = 11m, IrrecoverableDebtsDisallowable = 12m,
        ProfessionalFeesDisallowable = 13m, DepreciationDisallowable = 14m,
        OtherExpensesDisallowable = 15m
    }
};
var detailedJson = JsonSerializer.Serialize(detailed, options);
using var detailedDoc = JsonDocument.Parse(detailedJson);
var detailedRoot = detailedDoc.RootElement;
Assert(detailedRoot.GetProperty("periodIncome").TryGetProperty("other", out _) && !detailedJson.Contains("otherBusinessIncome"), "Income must use HMRC property 'other'.");
Assert(detailedJson.Contains("taxTakenOffTradingIncome") && detailedJson.Contains("irrecoverableDebts") && detailedJson.Contains("depreciation"), "Current cumulative fields are missing.");
Assert(detailedRoot.GetProperty("periodExpenses").EnumerateObject().Count() == 15, "Detailed expenses must expose all fifteen leaves.");
Assert(detailedRoot.GetProperty("periodDisallowableExpenses").EnumerateObject().Count() == 15, "All fifteen disallowable leaves must be represented.");
Assert(detailedRoot.GetProperty("periodExpenses").GetProperty("costOfGoods").GetDecimal() == -1m, "Signed expenses must be preserved.");
Assert(detailedRoot.GetProperty("periodIncome").GetProperty("turnover").GetDecimal() == 0m, "Explicit zero must remain explicit.");
Assert(!detailedJson.Contains("nino", StringComparison.OrdinalIgnoreCase) && !detailedJson.Contains("businessId") && !detailedJson.Contains("taxYear"), "Path parameters must not enter the cumulative body.");

var omitted = new CumulativeSubmission { PeriodIncome = new(), PeriodExpenses = new DetailedPeriodExpenses() };
var omittedJson = JsonSerializer.Serialize(omitted, options);
Assert(!omittedJson.Contains("turnover") && !omittedJson.Contains("periodDates"), "Nullable omissions must remain absent rather than zero/default objects.");
try
{
    JsonSerializer.Deserialize<CumulativeSubmission>("{\"periodDates\":{\"periodStartDate\":\"2025-04-06\"},\"periodIncome\":{},\"periodExpenses\":{}}", options);
    Assert(false, "A present periodDates object must require both dates.");
}
catch (JsonException)
{
    Assert(true, "A present periodDates object requires both dates.");
}

var consolidated = new CumulativeSubmission { PeriodIncome = new() { Turnover = 1m, Other = 0m }, PeriodExpenses = new ConsolidatedPeriodExpenses { ConsolidatedExpenses = -42.5m } };
var consolidatedJson = JsonSerializer.Serialize(consolidated, options);
Assert(consolidatedJson.Contains("consolidatedExpenses") && !consolidatedJson.Contains("costOfGoods"), "Consolidated expenses must be a distinct wire shape.");
Assert(typeof(PeriodExpenses).IsAbstract && !typeof(DetailedPeriodExpenses).IsAssignableFrom(typeof(ConsolidatedPeriodExpenses)), "Detailed and consolidated expenses must be mutually exclusive CLR shapes.");
var detailedRoundTrip = JsonSerializer.Deserialize<CumulativeSubmission>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cumulative-detailed.json")), options);
var consolidatedRoundTrip = JsonSerializer.Deserialize<CumulativeSubmission>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "cumulative-consolidated.json")), options);
Assert(detailedRoundTrip?.PeriodExpenses is DetailedPeriodExpenses, "A detailed cumulative GET response must deserialize to the detailed expense shape.");
Assert(consolidatedRoundTrip?.PeriodExpenses is ConsolidatedPeriodExpenses, "A consolidated cumulative GET response must deserialize to the consolidated expense shape.");

var annual = new AnnualSubmission2025
{
    Adjustments = new() { BasisAdjustment = -10m, GoodsAndServicesOwnUse = 20m },
    Allowances = new() { AnnualInvestmentAllowance = 0m, StructuredBuildingAllowance = [new() { Amount = 1m, Building = new() { Name = "Workshop", Postcode = "AA1 1AA" } }] },
    NonFinancials = new() { Class4NicsExemptionReason = "non-resident" }
};
var annualJson = JsonSerializer.Serialize(annual, options);
Assert(annualJson.Contains("adjustments") && annualJson.Contains("allowances") && annualJson.Contains("nonFinancials"), "Annual optional groups must serialize with authoritative names.");
Assert(!annualJson.Contains("businessDetailsChangedRecently") && !annualJson.Contains("privateUseAdjustment"), "Retired/non-contract annual fields must remain absent.");
Assert(AnnualEndpoints.Put2026Preview.Preview && AnnualEndpoints.Put2026Preview.RequestType == typeof(AnnualSubmission2026Preview), "The 2026-27 annual schema must remain explicitly preview-gated.");

Assert(JsonSerializer.Serialize(CalculationType.InYear, options) == "\"in-year\"", "in-year wire literal is incorrect.");
Assert(JsonSerializer.Serialize(CalculationType.IntentToFinalise, options) == "\"intent-to-finalise\"", "intent-to-finalise wire literal is incorrect.");
Assert(JsonSerializer.Serialize(CalculationType.IntentToAmend, options) == "\"intent-to-amend\"", "intent-to-amend wire literal is incorrect.");
var calculationId = JsonSerializer.Deserialize<CalculationIdResponse>("{\"calculationId\":\"abc\"}", options);
Assert(calculationId?.CalculationId == "abc", "Calculation trigger response must retain calculationId.");
Assert(typeof(TradeControl.Tax.UK.Hmrc.Sa.v1_0.Losses.V6.CreateLossClaimRequest) != typeof(TradeControl.Tax.UK.Hmrc.Sa.v1_0.Losses.V7.LossClaimsResource), "Loss v6 and v7 must remain distinct contract generations.");
Assert(LossV6Endpoints.CreateLossClaim.ApiVersion == "6.0" && LossV7Endpoints.Put.ApiVersion == "7.0", "Loss version selection must remain explicit.");
Assert(TaxLiabilityAdjustmentEndpoints.Put.ApiVersion == "1.0" && TaxLiabilityAdjustmentEndpoints.Put.PathTemplate.Contains("tax-liability/adjustments"), "Tax Liability Adjustments v1 metadata is missing.");

foreach (var fixture in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "*.json"))
{
    using var document = JsonDocument.Parse(File.ReadAllText(fixture));
    Assert(document.RootElement.ValueKind == JsonValueKind.Object, $"Fixture {Path.GetFileName(fixture)} must contain a JSON object.");
}

var obligation = JsonSerializer.Deserialize<IncomeAndExpenditureObligationsResponse>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "obligations-response.json")), options);
Assert(obligation?.Obligations[0].ObligationDetails[0].PeriodStartDate == new DateOnly(2025, 4, 6), "Obligations response dates did not deserialize.");
var account = JsonSerializer.Deserialize<BalanceAndTransactionsResponse>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "accounts-response.json")), options);
Assert(account?.BalanceDetails?.PayableAmount == 125.50m && account.BalanceDetails.TotalBalance == 150.50m, "Accounts wire response did not deserialize.");
AssertCompleteRoundTrip<BalanceAndTransactionsResponse>("accounts-response.json");
AssertCompleteRoundTrip<PaymentsAndAllocationsResponse>("accounts-payments-response-full.json");
AssertCompleteRoundTrip<BsasSummaryResponse>("bsas-response-full.json");
AssertCompleteRoundTrip<BissWireResponse>("biss-response-full.json");
AssertCompleteRoundTrip<BusinessListWireResponse>("business-details-list-response-full.json");
AssertCompleteRoundTrip<BusinessDetailWireResponse>("business-details-detail-response-full.json");
AssertCompleteRoundTrip<LadrElectionWireResponse>("business-details-ladr-response-full.json");
AssertCompleteRoundTrip<CalculationResponse2025>("calculation-response-2025-full.json");
AssertCompleteRoundTrip<CalculationResponse2026>("calculation-response-2026-full.json");

Console.WriteLine($"SA Objective 3 contract tests passed ({checks} assertions, {endpoints.Count} endpoint descriptors).");
