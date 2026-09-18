using System.Reflection;
using System.Text.Json;
using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Application.Preparation;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
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
