using System.Reflection;
using System.Text.Json;
using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

void AssertRejected(Action action, string message)
{
    assertions++;
    try { action(); }
    catch (ArgumentException) { return; }
    catch (InvalidOperationException) { return; }
    throw new InvalidOperationException(message);
}

var zero = TaxValue<decimal>.ExplicitZero(0m);
var absent = TaxValue<decimal>.Absent("No source fact.");
var unsupported = TaxValue<decimal>.Unsupported("Not supported by this profile.");
var invalid = TaxValue<decimal>.Invalid("Contradictory contributors.");
var notApplicable = TaxValue<decimal>.NotApplicable();
Assert(zero.HasValue && zero.State == TaxValueState.ExplicitZero && zero.Value == 0m,
    "Explicit zero was not retained as a value.");
Assert(!absent.HasValue && !unsupported.HasValue && !invalid.HasValue && !notApplicable.HasValue
    && new[] { absent.State, unsupported.State, invalid.State, notApplicable.State }.Distinct().Count() == 4,
    "Non-value source states can be confused.");
try
{
    _ = TaxValue<decimal>.ExplicitZero(1m);
    Assert(false, "A non-zero value was accepted as explicit zero.");
}
catch (ArgumentException) { Assert(true, "Explicit-zero invariant enforced."); }
try
{
    _ = new SourceKey("Server=example;Password=secret");
    Assert(false, "A connection string was accepted as a configured source key.");
}
catch (ArgumentException) { Assert(true, "Safe source-key invariant enforced."); }

var vat = ReadVatFixture();
Assert(vat.Subject.SubjectCode == "HOME" && vat.Subject.DisplayName == "Example Sole Trader",
    "Stable subject identity and display label were conflated.");
Assert(vat.VatDueSales.Value.State == TaxValueState.ExplicitZero && vat.TotalVatDue.Value.Value == 125.25m,
    "VAT source snapshot lost zero or decimal precision.");
Assert(vat.Provenance.SnapshotToken == "VAT-0001" && vat.Provenance.Facts.Count == 10,
    "VAT dataset/fact provenance is incomplete.");
var populatedVat = VatReturnPreparer.Populate(vat, "123456789", "26A1", true);
Assert(populatedVat.VatDueAcquisitions == 125.25m && populatedVat.TotalVatDue == 125.25m
    && populatedVat.VatReclaimedCurrPeriod == 25.25m && populatedVat.NetVatDue == 100m,
    "VAT boxes were not populated using explicit magnitude and arithmetic rules.");
var adjustedVat = vat with { VatAdjustment = new("VAT-ADJUSTMENT", "VAT adjustment",
    TaxValue<decimal>.Present(-0.05m), Provenance("vat-return", "VAT-ADJUSTMENT")) };
var adjusted = VatReturnPreparer.Populate(adjustedVat, "123456789", "26A1", true);
Assert(adjusted.VatDueAcquisitions == populatedVat.VatDueAcquisitions - 0.05m
    && adjusted.TotalVatDue == populatedVat.TotalVatDue - 0.05m
    && adjusted.NetVatDue == populatedVat.NetVatDue - 0.05m,
    "A signed VAT adjustment did not affect only boxes 2, 3 and 5.");
try
{
    _ = VatReturnPreparer.Populate(vat with
    {
        VatDueAcquisitions = vat.VatDueAcquisitions with { Value = TaxValue<decimal>.Present(-0.01m) },
        VatAdjustment = vat.VatAdjustment with { Value = TaxValue<decimal>.Present(-0.02m) }
    }, "123456789", "26A1", true);
    Assert(false, "A VAT adjustment was allowed to make box 2 negative.");
}
catch (InvalidOperationException) { Assert(true, "Negative adjusted box 2 was rejected."); }

var business = ReadBusinessFixture();
Assert(business.Facts.Count == 2 && business.Facts[0].Key.Value == "TURNOVER"
    && business.Facts[0].DisplayLabel == "Turnover", "Business stable keys and labels were conflated.");
Assert(business.Facts[0].Amount.Value == 1200.50m && business.Facts[1].Amount.State == TaxValueState.ExplicitZero,
    "Business-income money did not remain decimal or explicit zero.");

var minSource = CumulativeFixture(true);
var minSubmission = CumulativePeriodSummaryPreparer.Populate(minSource);
Assert(minSubmission.PeriodIncome.Turnover == 1000m && minSubmission.PeriodIncome.Other == 0m
    && minSubmission.PeriodExpenses is TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Cumulative.ConsolidatedPeriodExpenses
        { ConsolidatedExpenses: 165m },
    "The MIN cumulative profile did not preserve income, explicit zero and expense orientation.");
var stdSource = CumulativeFixture(false);
var stdSubmission = CumulativePeriodSummaryPreparer.Populate(stdSource);
Assert(stdSubmission.PeriodExpenses is TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Cumulative.DetailedPeriodExpenses
        { CostOfGoods: 100m, CarVanTravelExpenses: 20m, OtherExpenses: -2m, IrrecoverableDebts: 0m, Depreciation: 0m },
    "The STD cumulative profile did not map its exact detailed members and orientations.");
var roundedSource = stdSource with { Facts = stdSource.Facts.Select(fact => fact.Key.Value == "turnover"
    ? fact with { Amount = TaxValue<decimal>.Present(1000.005m) } : fact).ToArray() };
Assert(CumulativePeriodSummaryPreparer.Populate(roundedSource).PeriodIncome.Turnover == 1000.01m,
    "Cumulative values were not rounded to two decimals away from zero.");
try
{
    var invalidIncome = stdSource with { Facts = stdSource.Facts.Select(fact => fact.Key.Value == "turnover"
        ? fact with { Amount = TaxValue<decimal>.Present(-0.01m) } : fact).ToArray() };
    _ = CumulativePeriodSummaryPreparer.Populate(invalidIncome);
    Assert(false, "Negative cumulative turnover was accepted.");
}
catch (InvalidOperationException) { Assert(true, "Income wire-range validation fails closed."); }
var probeFacts = stdSource.Facts.Select(fact =>
{
    var mappingIndex = CumulativePopulationProfile.Mappings.ToList().FindIndex(mapping =>
        mapping.StableKey.Equals(fact.Key.Value, StringComparison.OrdinalIgnoreCase));
    var mapping = CumulativePopulationProfile.Mappings[mappingIndex];
    return mapping.ExpenseShape == CumulativeExpenseShape.Consolidated ? fact : fact with
    {
        Amount = TaxValue<decimal>.Present(mappingIndex + 1m)
    };
}).ToArray();
using (var probeJson = JsonDocument.Parse(SaJson.SerializeCanonical(
    CumulativePeriodSummaryPreparer.Populate(stdSource with { Facts = probeFacts }))))
foreach (var mapping in CumulativePopulationProfile.Mappings.Where(mapping =>
    mapping.Section == CumulativeTargetSection.Income || mapping.ExpenseShape == CumulativeExpenseShape.Detailed))
{
    var section = mapping.Section == CumulativeTargetSection.Income ? "periodIncome" : "periodExpenses";
    var expected = CumulativePopulationProfile.Mappings.ToList().FindIndex(candidate => candidate == mapping) + 1m;
    Assert(probeJson.RootElement.GetProperty(section).GetProperty(mapping.TargetMember).GetDecimal() == expected,
        $"Cumulative key '{mapping.StableKey}' did not populate '{section}.{mapping.TargetMember}'.");
}
try
{
    var both = minSource with { Facts = minSource.Facts.Select(fact =>
        fact.Key.Value == "costOfGoods" ? fact with { Amount = TaxValue<decimal>.Present(-1m) } : fact).ToArray() };
    _ = CumulativePeriodSummaryPreparer.Populate(both);
    Assert(false, "Consolidated and detailed cumulative expenses were accepted together.");
}
catch (InvalidOperationException) { Assert(true, "Mixed cumulative expense shapes fail closed."); }
try
{
    var unknown = minSource with { Facts = minSource.Facts.Append(new(new("unknownExpense"), "Unknown",
        TaxFactKind.Expense, TaxValue<decimal>.Present(-1m), Provenance("business-income", "unknownExpense"))).ToArray() };
    _ = CumulativePeriodSummaryPreparer.Populate(unknown);
    Assert(false, "An unknown cumulative fact key was accepted.");
}
catch (InvalidOperationException) { Assert(true, "Unknown cumulative fact keys fail closed."); }
var cumulativePreparer = new CumulativePeriodSummaryPreparer(new BusinessReader(minSource),
    new Readiness(new([])), new SourceContext(StatutoryFixture()),
    new FilingContext(new("QQ123456C", new("XQIS00000000001"), "CASH", "STANDARD", [])),
    new PreparedApiRequestPipeline());
var cumulativePrepared = await cumulativePreparer.PrepareAsync(new(new("sole-trader-standard"),
    new("UK-ITSA-SE-CUM"), minSource.Period, "2026-27"));
Assert(!cumulativePrepared.HasErrors && cumulativePrepared.BodyBytes.HasValue
    && cumulativePrepared.RelativePath == "/individuals/business/self-employment/QQ123456C/XQIS00000000001/cumulative/2026-27"
    && cumulativePrepared.BodySha256 == Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(cumulativePrepared.BodyBytes!.Value.AsSpan())),
    "The cumulative use case did not produce an exact immutable prepared request.");
var cumulativeBytes = cumulativePrepared.BodyBytes!.Value;
var standardPreparer = new CumulativePeriodSummaryPreparer(new BusinessReader(stdSource),
    new Readiness(new([])), new SourceContext(StatutoryFixture()),
    new FilingContext(new("QQ123456C", new("XQIS00000000001"), "CASH", "STANDARD", [])),
    new PreparedApiRequestPipeline());
var standardPrepared = await standardPreparer.PrepareAsync(new(new("sole-trader-standard"),
    new("UK-ITSA-SE-CUM"), stdSource.Period, "2026-27"));
Assert(!standardPrepared.HasErrors && standardPrepared.HasBody
    && !standardPrepared.BodyBytes!.Value.AsSpan().SequenceEqual(cumulativeBytes.AsSpan()),
    "The detailed cumulative profile did not produce its distinct prepared body.");
var vatPrepared = await new VatReturnPreparer(new VatReader(vat), new Readiness(new([])),
    new SourceContext(StatutoryFixture()), new PreparedApiRequestPipeline()).PrepareAsync(
        new(new("company-standard"), vat.Period, "26A1", true, "123456789"));
Assert(!vatPrepared.HasErrors && vatPrepared.HasBody
    && vatPrepared.RelativePath == "/organisations/vat/123456789/returns",
    "The VAT fixture did not pass through the complete offline preparation pipeline.");
var gateway = new CapturingGateway();
foreach (var prepared in new[] { vatPrepared, cumulativePrepared, standardPrepared })
{
    await gateway.SendAsync(prepared);
    Assert(ReferenceEquals(gateway.Received, prepared)
        && gateway.Received!.BodyBytes!.Value.AsSpan().SequenceEqual(prepared.BodyBytes!.Value.AsSpan()),
        "The Objective 4 handoff changed the prepared request instance or exact body bytes.");
}
Assert(vatPrepared.BodySha256 == "5B8376A5A0D38E781DB04880F212A82580726FF5789EA556C1F0C75564CB07C9"
    && cumulativePrepared.BodySha256 == "B451B9B75627D430B39231D8B2A2074F63DC26A988B60CC33C8866E92EAC4494"
    && standardPrepared.BodySha256 == "2823706A60A5AB43E48B3A00410752DF730A910DFB0521F4E5FA8282BCFBD302",
    "An approved VAT, MIN or STD offline prepared-body snapshot changed.");
var invalidTaxYear = await cumulativePreparer.PrepareAsync(new(new("sole-trader-standard"),
    new("UK-ITSA-SE-CUM"), minSource.Period, "2025-26"));
Assert(invalidTaxYear.HasErrors && !invalidTaxYear.HasBody
    && invalidTaxYear.Findings.Any(item => item.Code == "ITSA-PERIOD-OUTSIDE-TAX-YEAR"),
    "An invalid cumulative tax-year/period combination produced sendable bytes.");
var invalidStandardPeriod = await cumulativePreparer.PrepareAsync(new(new("sole-trader-standard"),
    new("UK-ITSA-SE-CUM"), new(new(2026, 4, 6), new(2026, 7, 4), TaxPeriodKind.Cumulative, "26-Q1"), "2026-27"));
Assert(invalidStandardPeriod.HasErrors && !invalidStandardPeriod.HasBody
    && invalidStandardPeriod.Findings.Any(item => item.Code == "ITSA-STANDARD-PERIOD-INVALID"),
    "A non-boundary standard cumulative period produced sendable bytes.");
var invalidFilingPreparer = new CumulativePeriodSummaryPreparer(new BusinessReader(minSource),
    new Readiness(new([])), new SourceContext(StatutoryFixture()),
    new FilingContext(new("QQ123456C", new("invalid-business"), "OTHER", "CALENDAR", [])),
    new PreparedApiRequestPipeline());
var invalidFiling = await invalidFilingPreparer.PrepareAsync(new(new("sole-trader-standard"),
    new("UK-ITSA-SE-CUM"), minSource.Period, "2026-27"));
Assert(invalidFiling.HasErrors && !invalidFiling.HasBody
    && new[] { "ITSA-BUSINESS-ID-INVALID", "ITSA-ACCOUNTING-BASIS-INVALID", "ITSA-CALENDAR-OBLIGATION-REQUIRED" }
        .All(code => invalidFiling.Findings.Any(item => item.Code == code)),
    "Invalid filing identifiers, basis or unverified calendar dates produced sendable bytes.");
var unsupportedTaxYear = await cumulativePreparer.PrepareAsync(new(new("sole-trader-standard"),
    new("UK-ITSA-SE-CUM"), new(new(2024, 4, 6), new(2024, 7, 5), TaxPeriodKind.Cumulative, "24-Q1"), "2024-25"));
Assert(unsupportedTaxYear.HasErrors && !unsupportedTaxYear.HasBody
    && unsupportedTaxYear.Findings.Any(item => item.Code == "ITSA-TAX-YEAR-UNSUPPORTED"),
    "A pre-cumulative API tax year produced sendable bytes.");

var vatReader = new VatReader(vat);
var vatSelector = new VatReturnSelector(new("company-standard"), vat.Period);
Assert(ReferenceEquals(await vatReader.ReadAsync(vatSelector), vat) && vatReader.LastSelector == vatSelector,
    "VAT source port did not preserve its typed selector.");
var businessReader = new BusinessReader(business);
var businessSelector = new BusinessIncomeSelector(new("sole-trader-standard"), business.TaxSourceCode,
    business.BusinessId, business.Period);
Assert(ReferenceEquals(await businessReader.ReadAsync(businessSelector), business)
    && businessReader.LastSelector == businessSelector, "Business-income port did not preserve its typed selector.");

var readiness = new Readiness(new([new(ReadinessScope.Mapping, SourceFindingSeverity.Error,
    "MAPPING-MISSING", "A required semantic mapping is missing.", "TURNOVER")]));
var result = await readiness.EvaluateAsync(new(new("sole-trader-standard"), business.Subject,
    business.Period, business.TaxSourceCode));
Assert(!result.IsReady && result.Findings.Single().Scope == ReadinessScope.Mapping,
    "Structured readiness findings did not block an invalid source.");

var assembly = typeof(VatReturnSource).Assembly;
var boundaryTypes = assembly.GetTypes().Where(x => x.Namespace == "TradeControl.Tax.Data").ToArray();
Assert(boundaryTypes.Length > 0 && boundaryTypes.All(x => !x.Name.Contains("Sql", StringComparison.OrdinalIgnoreCase)
    && !x.Name.Contains("Hmrc", StringComparison.OrdinalIgnoreCase)),
    "The neutral source boundary exposes infrastructure or authority vocabulary.");
Assert(boundaryTypes.SelectMany(x => x.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
    .All(x => !x.Name.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)),
    "A connection string crossed the Application source boundary.");
Assert(typeof(IVatReturnSourceReader).GetMethod("ReadAsync")!.GetParameters().Last().ParameterType == typeof(CancellationToken)
    && typeof(IBusinessIncomeSourceReader).GetMethod("ReadAsync")!.GetParameters().Last().ParameterType == typeof(CancellationToken),
    "Application source ports are not cancellation-aware.");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try
    {
        await vatReader.ReadAsync(vatSelector, cancellation.Token);
        Assert(false, "A cancelled source read continued.");
    }
    catch (OperationCanceledException) { Assert(true, "Cancellation propagated through the source port."); }
}

var describer = new BodylessRequestDescriber(new PreparedApiRequestPipeline());
var vatObligations = describer.Describe(new DescribeVatObligations(
    "123 456 789", new(2026, 4, 1), new(2026, 6, 30), "o"));
Assert(vatObligations.OperationId == "vat.obligations.list"
    && vatObligations.RelativePath == "/organisations/vat/123456789/obligations"
    && vatObligations.Query.Select(item => $"{item.Name}={item.Value}")
        .SequenceEqual(["from=2026-04-01", "to=2026-06-30", "status=O"])
    && vatObligations.Headers.Single().Value == "application/vnd.hmrc.1.0+json"
    && !vatObligations.HasBody && vatObligations.ContentType is null,
    "The VAT obligations description lost its path, ordered query, headers or bodyless semantics.");
var vatView = describer.Describe(new DescribeVatReturn("123456789", "26A1"));
Assert(vatView.RelativePath == "/organisations/vat/123456789/returns/26A1"
    && vatView.Query.Length == 0 && !vatView.HasBody,
    "The VAT view-return description is incorrect.");
var incomeObligations = describer.Describe(new DescribeIncomeTaxObligations(
    "qq 12 34 56 c", "SELF-EMPLOYMENT", "XQIS00000000001",
    new(2026, 4, 6), new(2027, 4, 5), "open"));
Assert(incomeObligations.RelativePath == "/obligations/details/QQ123456C/income-and-expenditure"
    && incomeObligations.Query.Select(item => item.Name)
        .SequenceEqual(["typeOfBusiness", "businessId", "fromDate", "toDate", "status"])
    && incomeObligations.Query.Last().Value == "Open"
    && incomeObligations.Headers.Single().Value == "application/vnd.hmrc.3.0+json"
    && !incomeObligations.HasBody && incomeObligations.BodySha256 is null,
    "The Income Tax obligations description lost its typed identifiers, ordered query or bodyless semantics.");
AssertRejected(() => describer.Describe(new DescribeVatObligations("123456789", new(2026, 4, 1))),
    "An unpaired VAT date filter was accepted.");
AssertRejected(() => describer.Describe(new DescribeVatReturn("12345678", "26A1")),
    "An invalid VAT registration was accepted.");
AssertRejected(() => describer.Describe(new DescribeIncomeTaxObligations(
        "QQ123456C", BusinessId: "XQIS00000000001")),
    "An Income Tax business identifier without its business type was accepted.");
AssertRejected(() => describer.Describe(new DescribeIncomeTaxObligations(
        "QQ123456C", Status: "Unknown")),
    "An invalid Income Tax obligation status was accepted.");

Console.WriteLine($"Application source vocabulary tests passed ({assertions} assertions)." );

static VatReturnSource ReadVatFixture()
{
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "vat-source.json")));
    var root = json.RootElement;
    var subject = Subject(root);
    var period = Period(root, TaxPeriodKind.Vat);
    var values = root.GetProperty("values").EnumerateArray().Select(x => x.GetDecimal()).ToArray();
    var keys = new[] { "VAT-DUE-SALES", "VAT-DUE-ACQUISITIONS", "TOTAL-VAT-DUE", "VAT-RECLAIMED",
        "NET-VAT-DUE", "SALES-EX-VAT", "PURCHASES-EX-VAT", "GOODS-SUPPLIED-EX-VAT", "ACQUISITIONS-EX-VAT" };
    var facts = keys.Select((key, index) => Provenance("vat-return", key)).ToArray();
    TaxFact<decimal> Fact(int index) => new(keys[index], keys[index],
        values[index] == 0m ? TaxValue<decimal>.ExplicitZero(0m) : TaxValue<decimal>.Present(values[index]), facts[index]);
    var adjustmentProvenance = Provenance("vat-return", "VAT-ADJUSTMENT");
    var adjustment = new TaxFact<decimal>("VAT-ADJUSTMENT", "VAT adjustment",
        TaxValue<decimal>.ExplicitZero(0m), adjustmentProvenance);
    var provenance = Dataset(root, "vat-return", facts.Append(adjustmentProvenance).ToArray());
    return new(subject, period, Fact(0), Fact(1), adjustment, Fact(2), Fact(3), Fact(4), Fact(5), Fact(6), Fact(7), Fact(8), provenance);
}

static BusinessIncomeSource ReadBusinessFixture()
{
    using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "business-income-source.json")));
    var root = json.RootElement;
    var facts = root.GetProperty("facts").EnumerateArray().Select(x =>
    {
        var key = x.GetProperty("key").GetString()!;
        var amount = x.GetProperty("amount").GetDecimal();
        return new BusinessIncomeFact(new(key), x.GetProperty("label").GetString()!,
            Enum.Parse<TaxFactKind>(x.GetProperty("kind").GetString()!),
            amount == 0m ? TaxValue<decimal>.ExplicitZero(0m) : TaxValue<decimal>.Present(amount),
            Provenance("business-income", key));
    }).ToArray();
    return new(Subject(root), new(root.GetProperty("businessId").GetString()!),
        new(root.GetProperty("taxSourceCode").GetString()!), Period(root, TaxPeriodKind.Cumulative), facts,
        Dataset(root, "business-income", facts.Select(x => x.Provenance).ToArray()));
}

static BusinessIncomeSource CumulativeFixture(bool consolidated)
{
    var period = new TaxReportingPeriod(new(2026, 4, 6), new(2026, 7, 5), TaxPeriodKind.Cumulative, "26-Q1");
    var subject = new TaxSubject("HOME", "Example Sole Trader", TaxSubjectKind.Person,
        TaxLegalForm.SoleTrader, "UK", "GBP");
    var facts = CumulativePopulationProfile.Mappings.Select(mapping =>
    {
        var active = mapping.Section == CumulativeTargetSection.Income
            || mapping.ExpenseShape == (consolidated ? CumulativeExpenseShape.Consolidated : CumulativeExpenseShape.Detailed);
        if (!consolidated && mapping.StableKey is "irrecoverableDebts" or "depreciation") active = false;
        var amount = mapping.StableKey switch
        {
            "turnover" => 1000m,
            "otherBusinessIncome" => 0m,
            "consolidatedExpenses" => 165m,
            "costOfGoods" => 100m,
            "carVanTravelExpenses" => 20m,
            "otherExpenses" => -2m,
            _ => 0m
        };
        var value = !active ? TaxValue<decimal>.Unsupported("Alternate cumulative profile.")
            : amount == 0m ? TaxValue<decimal>.ExplicitZero(0m) : TaxValue<decimal>.Present(amount);
        return new BusinessIncomeFact(new(mapping.StableKey), mapping.TargetMember,
            mapping.Section == CumulativeTargetSection.Income ? TaxFactKind.Income : TaxFactKind.Expense,
            value, Provenance("business-income", mapping.StableKey));
    }).ToArray();
    return new(subject, new("XQIS00000000001"), new("UK-ITSA-SE-CUM"), period, facts,
        new("fixture", "business-income", CumulativePopulationProfile.Version,
            new DateTimeOffset(2026, 9, 19, 12, 0, 0, TimeSpan.Zero),
            consolidated ? "MIN-0001" : "STD-0001", facts.Select(item => item.Provenance).ToArray()));
}

static StatutoryContextSnapshot StatutoryFixture()
{
    var version = new SourceVersion("fixture", "01", null);
    return new(new("HOME", "Example Sole Trader", 4, "UK", "UK", "GBP", null, null,
            "Trade", 1, "Trading", null, "Trading", [version]), [], [], [],
        new(new(2026, 4, 6), new(2027, 4, 5)));
}

static TaxSubject Subject(JsonElement root) => new(root.GetProperty("subjectCode").GetString()!,
    root.GetProperty("displayName").GetString()!, TaxSubjectKind.Person, TaxLegalForm.SoleTrader, "GB", "GBP");
static TaxReportingPeriod Period(JsonElement root, TaxPeriodKind kind) => new(
    DateOnly.Parse(root.GetProperty("start").GetString()!), DateOnly.Parse(root.GetProperty("end").GetString()!),
    kind, root.GetProperty("periodKey").GetString()!);
static FactProvenance Provenance(string dataset, string key) => new("fixture", dataset, key, key);
static DatasetProvenance Dataset(JsonElement root, string key, IReadOnlyList<FactProvenance> facts) =>
    new("fixture", key, "1", new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero),
        root.GetProperty("snapshotToken").GetString()!, facts);

sealed class VatReader(VatReturnSource source) : IVatReturnSourceReader
{
    public VatReturnSelector? LastSelector { get; private set; }
    public Task<VatReturnSource> ReadAsync(VatReturnSelector selector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSelector = selector;
        return Task.FromResult(source);
    }
}

sealed class BusinessReader(BusinessIncomeSource source) : IBusinessIncomeSourceReader
{
    public BusinessIncomeSelector? LastSelector { get; private set; }
    public Task<BusinessIncomeSource> ReadAsync(BusinessIncomeSelector selector, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastSelector = selector;
        return Task.FromResult(source);
    }
}

sealed class Readiness(SourceReadiness readiness) : ISourceReadinessEvaluator
{
    public Task<SourceReadiness> EvaluateAsync(SourceReadinessRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(readiness);
    }
}

sealed class SourceContext(StatutoryContextSnapshot context) : ISourceStatutoryContextReader
{
    public Task<StatutoryContextSnapshot> ReadAsync(SourceKey source, DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context);
    }
}

sealed class FilingContext(SelfEmploymentFilingContext context) : ISelfEmploymentFilingContextReader
{
    public Task<SelfEmploymentFilingContext> ReadAsync(SelfEmploymentFilingContextSelector selector,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(context);
    }
}

sealed class CapturingGateway : IPreparedApiRequestGateway
{
    public PreparedApiRequest? Received { get; private set; }
    public Task SendAsync(PreparedApiRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Received = request;
        return Task.CompletedTask;
    }
}
