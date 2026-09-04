using System.Text;
using System.Xml.Linq;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Projection;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Validation;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis5_9;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026.Generated;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Submission.V2026;

var assertions = 0;
var accounts = FixtureAccounts();
var validator = new CompanyContractValidator();
Assert(validator.Validate(accounts).IsValid, "Valid micro-company accounts were rejected.");

var accountsProjection = new StatutoryAccountsProjection();
var fullReport = accountsProjection.Project(accounts, false, "Example Limited accounts");
var filletedReport = accountsProjection.Project(accounts, true, "Example Limited filleted accounts");
Assert(fullReport.Facts.Count == filletedReport.Facts.Count + 12, "Filleting did not remove the current and comparative income-statement facts.");
Assert(fullReport.Facts.Any(x => x.Context.Period is XbrlPeriod.Instant p && p.Date == accounts.ComparativePeriod!.End), "Comparative instant facts are absent.");
Assert(fullReport.Facts.Any(x => x.Context.Period is XbrlPeriod.Duration p && p.Start == accounts.ComparativePeriod!.Start), "Comparative duration facts are absent.");
Assert(accounts.IncomeStatement.Turnover.Current == 120000m, "Filleting mutated the source accounts.");
Assert(validator.Validate(fullReport).IsValid, "Full accounts XBRL facts failed validation.");
Assert(validator.Validate(filletedReport).IsValid, "Filleted accounts XBRL facts failed validation.");

var ixBuilder = new IxbrlDocumentBuilder();
var fullIx = ixBuilder.Build(fullReport, "accounts.html");
var fullIxAgain = ixBuilder.Build(fullReport, "accounts.html");
var filletedIx = ixBuilder.Build(filletedReport, "accounts-filleted.html");
Assert(fullIx.Content.SequenceEqual(fullIxAgain.Content), "iXBRL serialization is not deterministic.");
Assert(fullIx.Sha256 == fullIxAgain.Sha256, "iXBRL digest is not deterministic.");
Assert(fullIx.Sha256.Length == 64, "SHA-256 digest has the wrong length.");
var ixXml = XDocument.Parse(Encoding.UTF8.GetString(fullIx.Content));
XNamespace ix = "http://www.xbrl.org/2013/inlineXBRL";
Assert(ixXml.Descendants(ix + "nonFraction").Any(), "iXBRL contains no numeric facts.");
Assert(ixXml.Descendants(ix + "nonNumeric").Any(), "iXBRL contains no non-numeric facts.");
Assert(Encoding.UTF8.GetString(fullIx.Content).Contains("120000", StringComparison.Ordinal), "Displayed turnover is absent from full accounts.");
Assert(!Encoding.UTF8.GetString(filletedIx.Content).Contains("120000", StringComparison.Ordinal), "Filleted accounts expose turnover.");

var chFiling = new CompaniesHouseAccountsFiling(accounts, DeliveredAccountsProfile.Filleted,
    new RegistrarStatements(true, true, true), filletedIx);
var chPackage = new CompaniesHouseFilingPackage("ENV-000001", chFiling);
var chSerializer = new CompaniesHouseEnvelopeSerializer();
var chXml = chSerializer.Serialize(chPackage);
Assert(chXml.Content.SequenceEqual(chSerializer.Serialize(chPackage).Content), "Companies House envelope is not deterministic.");
var chDocument = XDocument.Parse(Encoding.UTF8.GetString(chXml.Content));
Assert(chDocument.Descendants().Any(x => x.Name.LocalName == "CompanyNumber" && x.Value == "01234567"), "Companies House company number is absent.");
Assert(chDocument.Descendants().Any(x => x.Name.LocalName == "Delivery" && x.Value == "Filleted"), "Companies House delivery profile is absent.");
Assert(CompaniesHouseEndpointSet.SubmitAccounts.RequiresStatusPolling, "Companies House submission must expose polling semantics.");

var computation = FixtureComputation(accounts.Period);
Assert(computation.AdjustedTradingProfit == 30000m, "Adjusted trading profit calculation changed.");
var computationReport = new CorporationTaxComputationProjection().Project(computation, accounts.Company.CompanyNumber);
Assert(validator.Validate(computationReport).IsValid, "Computation XBRL facts failed validation.");
var computationIx = ixBuilder.Build(computationReport, "computation.html");
Assert(computationIx.Content.Length > 0, "Computation iXBRL is empty.");

var ct600 = new Ct600Return(accounts.Company.CompanyName, accounts.Company.CompanyNumber, "1234567890", accounts.Period,
    120000m, 28000m, 30000m, 5700m, 5700m, true, true,
    new Ct600A(10000m, 3375m, 3375m), "Jane Director", new DateOnly(2026, 7, 31));
var ctPackage = new CorporationTaxReturnPackage(ct600, computation, fullIx, computationIx, []);
Assert(validator.Validate(ctPackage).IsValid, "Valid Corporation Tax package was rejected.");
var ctSerializer = new CorporationTaxPackageSerializer();
var ctXml = ctSerializer.Serialize(ctPackage, "CT-CORR-1");
Assert(ctXml.Content.SequenceEqual(ctSerializer.Serialize(ctPackage, "CT-CORR-1").Content), "Corporation Tax serialization is not deterministic.");
var ctDocument = XDocument.Parse(Encoding.UTF8.GetString(ctXml.Content));
Assert(ctDocument.Root?.Name.LocalName == "IRenvelope", "Corporation Tax root is not IRenvelope.");
Assert(ctDocument.Root?.Name.NamespaceName == Ct600Contract.Namespace, "Corporation Tax namespace changed.");
Assert(ctDocument.Descendants().Any(x => x.Name.LocalName == "CT600A"), "CT600A is absent from the package.");
Assert(CorporationTaxEndpointSet.Submit.ContractVersion.Contains("1.994", StringComparison.Ordinal), "CT600 endpoint metadata is not pinned to RIM 1.994.");
Assert(typeof(IRenvelope).GetCustomAttributes(false).Any(), "Official generated CT600 wire family is absent.");

var brokenResult = validator.Validate(ctPackage with { Return = ct600 with { TaxPayable = 1m } });
Assert(!brokenResult.IsValid && brokenResult.Findings.Any(x => x.Code == "CT005"), "Tax payable mismatch was not rejected.");
var brokenA = ct600 with { SupplementaryPageA = new Ct600A(10000m, 0m, 0m) };
Assert(validator.Validate(ctPackage with { Return = brokenA }).Findings.Any(x => x.Code == "CT600A001"), "Unsupported CT600A values were not rejected.");

var longPeriod = new ReportingPeriod(new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30));
var allocation = CorporationTaxPeriodAllocation.Split(longPeriod);
Assert(allocation.CorporationTaxPeriods.Count == 2, "Long accounts period did not create two CT periods.");
Assert(allocation.CorporationTaxPeriods[0].End.AddDays(1) == allocation.CorporationTaxPeriods[1].Start, "Split CT periods are not contiguous.");
Assert(allocation.CorporationTaxPeriods.All(x => x.InclusiveDays <= 366), "A CT period exceeds twelve months.");

Assert(new XbrlQName("urn:test", "A") == new XbrlQName("urn:test", "A"), "QName value equality failed.");
Assert(new XbrlQName("urn:test:one", "A") != new XbrlQName("urn:test:two", "A"), "QName ignored its namespace.");
Assert(Frc2026Catalog.MicroEntity.Concepts["Turnover"].Name.NamespaceUri == Frc2026Catalog.CoreNamespace, "FRC concept namespace is not pinned.");
Assert(CompanyContractCatalog.Sources.Count == 3, "Authoritative source provenance is incomplete.");
Assert(CompanyContractCatalog.Sources.All(x => x.Sha256.Length == 64), "A source checksum is invalid.");
Assert(CompanyContractCatalog.Ct600V1994.Version == "1.994", "CT600 provenance version changed.");
Assert(CompanyContractCatalog.CompaniesHouseTis59.Version == "5.9", "Companies House provenance version changed.");
Assert(CompanyContractRegistry.SelectProduction("hmrc-ct600", new DateOnly(2026, 4, 7)).Version == "1.994", "Effective-date version selection failed.");
Assert(!CompanyContractRegistry.CompaniesHouseReplacement.SubmissionReady && CompanyContractRegistry.CompaniesHouseReplacement.Status == ContractStatus.Preview,
    "Future Companies House contract became production-selectable.");
Assert(!CompanyContractRegistry.HmrcComputationTaxonomy2025.SubmissionReady, "Derived computation QNames were incorrectly marked submission-ready.");

var manifestPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "manifest.json");
Assert(File.Exists(manifestPath) && File.ReadAllText(manifestPath).Contains("CT600", StringComparison.OrdinalIgnoreCase), "Offline fixture manifest was not deployed.");
using (var safeFixture = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CompaniesHouse", "accepted-status.xml")))
    Assert(SecureXml.Parse(safeFixture).Root?.Name.LocalName == "Status", "Offline status fixture did not parse.");
try
{
    using var hostile = new MemoryStream(Encoding.UTF8.GetBytes("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>"));
    SecureXml.Parse(hostile);
    throw new InvalidOperationException("DTD input was accepted.");
}
catch (System.Xml.XmlException) { assertions++; }

try
{
    SupportedCompanyProfile.Require(false, "group accounts");
    throw new InvalidOperationException("Unsupported scenario was silently accepted.");
}
catch (UnsupportedStatutoryScenarioException ex)
{
    Assert(ex.Scenario == "group accounts", "Unsupported scenario identity was lost.");
}

Console.WriteLine($"Company Objective 3 contract tests passed ({assertions} assertions).\n" +
    $"Artifacts: accounts={fullIx.Sha256}, computation={computationIx.Sha256}, CH={chXml.Sha256}, CT={ctXml.Sha256}");

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

static StatutoryAccounts FixtureAccounts()
{
    ComparativeAmount A(decimal current, decimal comparative) => new(current, comparative);
    return new(
        new CompanyIdentity("Example Limited", "01234567"),
        new ReportingPeriod(new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30)),
        new ReportingPeriod(new DateOnly(2024, 7, 1), new DateOnly(2025, 6, 30)),
        new AccountsProfile(ReportingFramework.Frs105, AccountsType.MicroEntity, AuditStatus.UnauditedExempt, true, true),
        new StatementOfFinancialPosition(A(12000, 10000), A(35000, 30000), A(1000, 800), A(18000, 16000),
            A(18000, 14800), A(30000, 24800), A(5000, 4000), A(0, 0), A(0, 0), A(25000, 20800), A(25000, 20800)),
        new IncomeStatement(A(120000, 100000), A(1000, 500), A(60000, 50000), A(33000, 29000), A(5700, 4085), A(22300, 17415)),
        new AccountsNotes("Software development", "FRS 105 historical cost basis", 3,
            [new DirectorAdvance("Jane Director", 2000, 10000, 7000, 5000, "Interest free and repayable on demand")], []),
        new AccountsApproval(new DateOnly(2026, 7, 31), "Jane Director"));
}

static CorporationTaxComputation FixtureComputation(ReportingPeriod period) => new(
    period, 28000m,
    [new TaxAdjustment("Depreciation", 5000m)],
    [new TaxAdjustment("Non-trading income", 1000m)],
    new CapitalAllowanceSchedule(2000m, 0m, 0m),
    new LossReliefSchedule(0m, 0m, 0m, 0m),
    30000m, 0.19m, 5700m, 0m, 5700m);
