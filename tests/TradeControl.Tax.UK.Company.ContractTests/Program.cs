using System.Text;
using System.Xml.Linq;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Projection;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Validation;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;
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
Assert(fullReport.Facts.Single(x => x.Concept.LocalName == "DateAuthorisationFinancialStatementsForIssue").Context.Period
        is XbrlPeriod.Instant { Date: var approvalContextDate } && approvalContextDate == accounts.Period.End,
    "The accounts-authorisation fact is not associated with the current accounting period.");
Assert(fullReport.Facts.Single(x => x.Concept.LocalName == "StartDateForPeriodCoveredByReport").Context.Period
        is XbrlPeriod.Instant { Date: var periodStartContextDate } && periodStartContextDate == accounts.Period.End,
    "The report-start fact is not associated with the current accounting period.");
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
XNamespace link = "http://www.xbrl.org/2003/linkbase";
Assert(ixXml.Descendants(ix + "nonFraction").Any(), "iXBRL contains no numeric facts.");
Assert(ixXml.Descendants(ix + "nonNumeric").Any(), "iXBRL contains no non-numeric facts.");
var ixHeader = ixXml.Descendants(ix + "header").Single();
Assert(ixHeader.Parent?.Name.LocalName == "div"
    && ixHeader.Parent.Attribute("style")?.Value.Contains("display:none", StringComparison.Ordinal) == true,
    "The required ix:header is not hidden by its XHTML container.");
Assert(ixHeader.Elements().Select(x => x.Name).SequenceEqual([ix + "references", ix + "resources"]),
    "The ix:header does not contain references followed by resources.");
Assert(ixHeader.Element(ix + "references")?.Elements(link + "schemaRef").Count() == 1
    && ixHeader.Element(ix + "resources")?.Elements(link + "schemaRef").Any() == false,
    "The taxonomy schemaRef is not contained exclusively by ix:references.");
Assert(ixHeader.Element(ix + "references")?.Element(link + "schemaRef")?.Attribute(XNamespace.Get("http://www.w3.org/1999/xlink") + "href")?.Value
    == "https://xbrl.frc.org.uk/FRS-102/2026-01-01/FRS-102-2026-01-01.xsd",
    "The FRC 2026 taxonomy entry point is not the directly retrievable HTTPS resource.");
var taxonomy = Frc2026Catalog.MicroEntity.Concepts;
Assert(taxonomy["CompanyName"].Name == new XbrlQName(Frc2026Catalog.BusinessNamespace, "EntityCurrentLegalOrRegisteredName")
    && taxonomy["CompanyNumber"].Name == new XbrlQName(Frc2026Catalog.BusinessNamespace, "UKCompaniesHouseRegisteredNumber")
    && taxonomy["PrincipalActivity"].Name == new XbrlQName(Frc2026Catalog.BusinessNamespace, "DescriptionPrincipalActivities"),
    "FRC 2026 business-identification concepts are not mapped to the business taxonomy namespace.");
Assert(taxonomy["TaxOnProfit"].Name.LocalName == "TaxTaxCreditOnProfitOrLossOnOrdinaryActivities"
    && taxonomy["ProfitLoss"].Name.LocalName == "ProfitLoss"
    && taxonomy["CreditorsWithinOneYear"].Name.LocalName == "CurrentLiabilities"
    && taxonomy["CreditorsAfterOneYear"].Name.LocalName == "Non-currentLiabilities"
    && taxonomy["CapitalAndReserves"].Name.LocalName == "Equity",
    "FRC 2026 renamed core concepts are not mapped to their published taxonomy names.");
var fixedConcepts = new HashSet<string>(StringComparer.Ordinal)
{
    "bus:AccountingStandardsApplied", "bus:AccountsStatusAuditedOrUnaudited", "bus:AccountsType",
    "bus:EntityTradingStatus", "core:DirectorSigningFinancialStatements"
};
Assert(ixXml.Descendants(ix + "nonNumeric")
        .Where(x => fixedConcepts.Contains(x.Attribute("name")?.Value ?? string.Empty))
        .All(x => x.Attribute(XNamespace.Xml + "lang") is null && string.IsNullOrEmpty(x.Value)),
    "A zero-length FRC fixedItemType fact carries text or xml:lang.");
Assert(Encoding.UTF8.GetString(fullIx.Content).Contains("120,000.00", StringComparison.Ordinal), "Displayed turnover is absent from full accounts.");
Assert(!Encoding.UTF8.GetString(filletedIx.Content).Contains("120,000.00", StringComparison.Ordinal), "Filleted accounts expose turnover.");
var filletedXml = XDocument.Parse(Encoding.UTF8.GetString(filletedIx.Content));
var filletedText = filletedXml.Root!.Value;
XNamespace xhtml = "http://www.w3.org/1999/xhtml";
XNamespace ixt = "http://www.xbrl.org/inlineXBRL/transformation/2020-02-12";
Assert(!filletedXml.Descendants().Any(element => element.Name.Namespace == xhtml
        && element.Name.LocalName is "main" or "section")
    && filletedXml.Descendants(xhtml + "style").Single().Attribute("type")?.Value == "text/css",
    "The accounts presentation uses XHTML content rejected by the Companies House gateway schema.");
Assert(filletedText.Contains("Balance sheet as at", StringComparison.Ordinal)
    && filletedText.Contains("Fixed assets", StringComparison.Ordinal)
    && filletedText.Contains("Creditors: amounts falling due within one year", StringComparison.Ordinal)
    && filletedText.Contains("Capital and reserves", StringComparison.Ordinal),
    "Filleted iXBRL is not presented as a recognisable statutory balance sheet.");
Assert(filletedText.Contains("Accounts status: Unaudited — audit exempt, no accountants' report",
           StringComparison.Ordinal)
       && filletedText.Contains("Accounts type: Filleted accounts", StringComparison.Ordinal)
       && filletedText.Contains("Accounting standard: FRS 105 micro-entities", StringComparison.Ordinal)
       && filletedText.Contains("Trading status: Trading", StringComparison.Ordinal)
       && filletedText.Contains("Dormant company: false", StringComparison.Ordinal),
    "The filing-profile classifications are not intelligible in the human-readable accounts.");
Assert(filletedXml.Descendants(ix + "nonNumeric")
        .Where(x => fixedConcepts.Contains(x.Attribute("name")?.Value ?? string.Empty))
        .All(x => x.Attribute(XNamespace.Xml + "lang") is null && string.IsNullOrEmpty(x.Value)),
    "Human-readable classifications changed the accepted fixedItemType XBRL facts.");
Assert(filletedXml.Root.GetNamespaceOfPrefix("ixt") == ixt
    && filletedXml.Descendants(xhtml + "th").Any(x => x.Value == "2026 £'s")
    && filletedXml.Descendants(xhtml + "th").Any(x => x.Value == "2025 £'s"),
    "The statutory accounts headings do not identify the sterling presentation unit.");
Assert(filletedXml.Descendants(ix + "nonFraction")
        .Where(x => x.Attribute("unitRef")?.Value is not null)
        .Any(x => x.Value == "35,000.00" && x.Attribute("format")?.Value == "ixt:num-dot-decimal"),
    "Monetary facts are not displayed with accounting separators and two decimal places.");
var currentAssetsConcept = taxonomy["CurrentAssets"].Name;
var negativePresentationReport = fullReport with
{
    Facts = fullReport.Facts.Select(fact => fact.Concept == currentAssetsConcept
            && fact.Context.Period is XbrlPeriod.Instant { Date: var date }
            && date == accounts.Period.End
        ? fact with { Value = new XbrlValue.Monetary(-35000.5m) }
        : fact).ToArray()
};
var negativePresentationXml = XDocument.Parse(Encoding.UTF8.GetString(
    ixBuilder.Build(negativePresentationReport, "negative-presentation.html").Content));
var negativePresentedFact = negativePresentationXml.Descendants(ix + "nonFraction")
    .Single(x => x.Attribute("name")?.Value == "core:CurrentAssets" && x.Attribute("sign")?.Value == "-");
Assert(negativePresentedFact.Value == "35,000.50"
    && negativePresentedFact.Attribute("format")?.Value == "ixt:num-dot-decimal"
    && negativePresentedFact.Parent?.Value == "(35,000.50)",
    "Negative monetary facts are not displayed in brackets while preserving the iXBRL sign.");
Assert(filletedText.Contains("Jane Director", StringComparison.Ordinal)
    && filletedText.Contains("These accounts have been prepared in accordance with the micro-entity provisions.", StringComparison.Ordinal),
    "The visible accounts omit their approval or statutory statements.");
Assert(filletedXml.Descendants(ix + "nonFraction").Count() + filletedXml.Descendants(ix + "nonNumeric").Count()
    == filletedReport.Facts.Count, "The visible statutory accounts do not render every projected fact exactly once.");
Assert(filletedReport.Facts.Any(x => x.Concept.LocalName == "StatementThatAccountsHaveBeenPreparedInAccordanceWithProvisionsSmallCompaniesRegime"),
    "TIS 6.0 micro-entity provisions statement is absent.");
Assert(filletedReport.Facts.Any(x => x.Concept.LocalName == "StatementThatCompanyEntitledToExemptionFromAuditUnderSection477CompaniesAct2006RelatingToSmallCompanies"),
    "TIS 6.0 section 477 statement is absent.");
Assert(filletedReport.Facts.Any(x => x.Concept.LocalName == "AccountsType" && x.Context.Dimensions?.Single().Member.LocalName == "FilletedAccounts"),
    "The filing accounts type dimension is absent.");
Assert(filletedReport.Facts.Any(x => x.Concept.LocalName == "EntityDormantTruefalse" && x.Value is XbrlValue.Boolean { Value: false }),
    "The mandatory non-dormant status fact is absent.");

var chFiling = new CompaniesHouseAccountsFiling(accounts, new RegistrarStatements(true, true, true), filletedIx);
var chPackage = new CompaniesHouseFilingPackage("ABC001", "UNALLOCATED", chFiling);
var chSerializer = new CompaniesHouseEnvelopeSerializer();
var chXml = chSerializer.Serialize(chPackage);
Assert(chXml.Content.SequenceEqual(chSerializer.Serialize(chPackage).Content), "Companies House envelope is not deterministic.");
var chDocument = XDocument.Parse(Encoding.UTF8.GetString(chXml.Content));
Assert(chDocument.Descendants().Any(x => x.Name.LocalName == "CompanyNumber" && x.Value == "01234567"), "Companies House company number is absent.");
Assert(chDocument.Descendants().Any(x => x.Name.LocalName == "FormSubmission" && x.Name.NamespaceName == CompaniesHouseEnvelopeSerializer.FormSubmissionNamespace),
    "Published FormSubmission body is absent.");
Assert(chDocument.Descendants().Single(x => x.Name.LocalName == "Class").Value == "Accounts"
    && chDocument.Descendants().Single(x => x.Name.LocalName == "FormIdentifier").Value == "Accounts",
    "The accounts filing does not use the observed Companies House Accounts routing pair.");
Assert(chDocument.Descendants().Any(x => x.Name.LocalName == "ContentType" && x.Value == "application/xml")
    && chDocument.Descendants().Any(x => x.Name.LocalName == "Category" && x.Value == "ACCOUNTS"),
    "Published accounts attachment metadata is absent.");
Assert(!chDocument.Descendants().Any(x => x.Name.LocalName is "CompanyAccounts" or "Delivery" or "SenderDetails" or "CompanyAuthenticationCode"),
    "The preview contains an obsolete logical body or transport-time credentials.");
var embeddedAccounts = Convert.FromBase64String(chDocument.Descendants().Single(x => x.Name.LocalName == "Data").Value);
Assert(embeddedAccounts.SequenceEqual(filletedIx.Content), "FormSubmission did not preserve the exact iXBRL bytes.");
Assert(validator.Validate(chPackage).IsValid, "Valid Companies House filing package was rejected.");
var invalidChPackage = chPackage with { Filing = chFiling with { Statements = new(false, true, true) } };
Assert(validator.Validate(invalidChPackage).Findings.Any(x => x.Code == "CH004"),
    "A missing Companies House micro-entity statement was not rejected.");
Assert(CompaniesHouseEndpointSet.SubmitAccounts.RequiresStatusPolling, "Companies House submission must expose polling semantics.");
var specificStatus = new CompaniesHouseSubmissionStatusRequest("PRESENTER01", CompaniesHouseStatusQueryScope.Submission, "ABC001");
var statusRequest = chSerializer.SerializeStatusRequest(specificStatus);
Assert(XDocument.Parse(Encoding.UTF8.GetString(statusRequest.Content)).Descendants().Any(x => x.Name.LocalName == "GetSubmissionStatus"),
    "GetSubmissionStatus request contract is absent.");
Assert(!specificStatus.RequiresStatusAcknowledgement
    && new CompaniesHouseSubmissionStatusRequest("PRESENTER01", CompaniesHouseStatusQueryScope.Presenter).RequiresStatusAcknowledgement,
    "Conditional StatusAck semantics do not distinguish specific and general polls.");
Assert(XDocument.Parse(Encoding.UTF8.GetString(chSerializer.SerializeStatusAcknowledgement().Content)).Descendants()
    .Any(x => x.Name.LocalName == "StatusAck" && !x.HasElements), "The conditional StatusAck contract is not empty.");
var statusXml = Encoding.UTF8.GetBytes("<SubmissionStatus xmlns='http://xmlgw.companieshouse.gov.uk'><Status><SubmissionNumber>ABC001</SubmissionNumber><StatusCode>REJECT</StatusCode><CompanyNumber>01234567</CompanyNumber><Rejections><Reject><RejectCode>100</RejectCode><Description>Rejected</Description></Reject></Rejections></Status></SubmissionStatus>");
Assert(chSerializer.ParseSubmissionStatuses(statusXml).Single().StatusCode == CompaniesHouseSubmissionStatusCode.Reject,
    "Published rejection status was not parsed.");

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
Assert(CorporationTaxEndpointSet.Submit.Protocol == "Transaction Engine XML"
    && CorporationTaxEndpointSet.Submit.RequiresStatusPolling,
    "Corporation Tax submission must expose its Transaction Engine XML conversation semantics.");
Assert(typeof(IRenvelope).GetCustomAttributes(false).Any(), "Official generated CT600 wire family is absent.");

var brokenResult = validator.Validate(ctPackage with { Return = ct600 with { TaxPayable = 1m } });
Assert(!brokenResult.IsValid && brokenResult.Findings.Any(x => x.Code == "CT005"), "Tax payable mismatch was not rejected.");
var brokenComputation = computation with { TaxableTotalProfits = computation.TaxableTotalProfits + 1m };
Assert(validator.Validate(ctPackage with { Computation = brokenComputation }).Findings.Any(x => x.Code == "CT010"),
    "A computation with unreconciled taxable profits was not rejected.");
var brokenCharge = computation with { CorporationTaxChargeable = computation.CorporationTaxChargeable + 1m };
Assert(validator.Validate(ctPackage with { Computation = brokenCharge }).Findings.Any(x => x.Code == "CT012"),
    "A computation with an irreconcilable Corporation Tax charge was not rejected.");
var brokenLosses = computation with { Losses = new(100m, 20m, 10m, 200m) };
Assert(validator.Validate(ctPackage with { Computation = brokenLosses }).Findings.Any(x => x.Code == "CT009"),
    "An irreconcilable loss schedule was not rejected.");
var brokenA = ct600 with { SupplementaryPageA = new Ct600A(10000m, 0m, 0m) };
Assert(validator.Validate(ctPackage with { Return = brokenA }).Findings.Any(x => x.Code == "CT600A001"), "Unsupported CT600A values were not rejected.");
var negativeA = ct600 with { SupplementaryPageA = new Ct600A(-1m, 0m, 0m) };
Assert(validator.Validate(ctPackage with { Return = negativeA }).Findings.Any(x => x.Code == "CT600A002"),
    "Negative CT600A values were not rejected.");
var noA = ctPackage with { Return = ct600 with { SupplementaryPageA = null } };
Assert(!XDocument.Parse(Encoding.UTF8.GetString(ctSerializer.Serialize(noA, "CT-CORR-NO-A").Content))
        .Descendants().Any(x => x.Name.LocalName == "CT600A"),
    "CT600A was serialized when the supplementary page was not applicable.");

var longPeriod = new ReportingPeriod(new DateOnly(2025, 1, 1), new DateOnly(2026, 6, 30));
var allocation = CorporationTaxPeriodAllocation.Split(longPeriod);
Assert(allocation.CorporationTaxPeriods.Count == 2, "Long accounts period did not create two CT periods.");
Assert(allocation.CorporationTaxPeriods[0].End.AddDays(1) == allocation.CorporationTaxPeriods[1].Start, "Split CT periods are not contiguous.");
Assert(allocation.CorporationTaxPeriods.All(x => x.InclusiveDays <= 366), "A CT period exceeds twelve months.");

Assert(new XbrlQName("urn:test", "A") == new XbrlQName("urn:test", "A"), "QName value equality failed.");
Assert(new XbrlQName("urn:test:one", "A") != new XbrlQName("urn:test:two", "A"), "QName ignored its namespace.");
Assert(Frc2026Catalog.MicroEntity.Concepts["Turnover"].Name.NamespaceUri == Frc2026Catalog.CoreNamespace, "FRC concept namespace is not pinned.");
Assert(Frc2026Catalog.MicroEntity.Concepts["AccrualsDeferredIncome"].Name.LocalName == "AccruedLiabilitiesDeferredIncome",
    "The accruals and deferred-income balance does not use the declared FRC 2026 concept.");
Assert(CompanyContractCatalog.Sources.Count == 3, "Authoritative source provenance is incomplete.");
Assert(CompanyContractCatalog.Sources.All(x => x.Sha256.Length == 64), "A source checksum is invalid.");
Assert(CompanyContractCatalog.Ct600V1994.Version == "1.994", "CT600 provenance version changed.");
Assert(CompanyContractCatalog.CompaniesHouseTis60.Version == "6.0", "Companies House provenance version changed.");
Assert(CompanyContractRegistry.SelectProduction("hmrc-ct600", new DateOnly(2026, 4, 7)).Version == "1.994", "Effective-date version selection failed.");
Assert(CompanyContractRegistry.SelectForPreview("companies-house-accounts", "TIS-6.0", new DateOnly(2026, 10, 3)).Status == ContractStatus.Production,
    "The effective Companies House production contract was not selected for preview.");
Assert(CompanyContractRegistry.SelectForPreview("companies-house-accounts", "future-api", new DateOnly(2026, 9, 13), true).Status == ContractStatus.Preview,
    "Explicit future-contract preview opt-in was rejected.");
AssertUnsupported(() => CompanyContractRegistry.SelectForPreview("companies-house-accounts", "future-api", new DateOnly(2026, 9, 13)),
    "A future Companies House contract was selected without preview opt-in.");
AssertUnsupported(() => CompanyContractRegistry.SelectForPreview("companies-house-accounts", "TIS-6.0", new DateOnly(2026, 9, 24)),
    "TIS 6.0 was selected before its effective date.");
AssertUnsupported(() => CompanyContractRegistry.SelectForPreview("companies-house-accounts", "unknown", new DateOnly(2026, 9, 13), true),
    "An unregistered Companies House contract was selected.");
Assert(!CompanyContractRegistry.CompaniesHouseReplacement.SubmissionReady && CompanyContractRegistry.CompaniesHouseReplacement.Status == ContractStatus.Preview,
    "Future Companies House contract became production-selectable.");
Assert(!CompanyContractRegistry.HmrcComputationTaxonomy2025.SubmissionReady, "Derived computation QNames were incorrectly marked submission-ready.");
Assert(CompanyServiceCoverageCatalog.Services.All(x => Enum.IsDefined(x.Disposition)), "A corporate service has no coverage disposition.");

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

Console.WriteLine($"Company contract tests passed ({assertions} assertions).\n" +
    $"Artifacts: accounts={fullIx.Sha256}, computation={computationIx.Sha256}, CH={chXml.Sha256}, CT={ctXml.Sha256}");

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

void AssertUnsupported(Action action, string message)
{
    try
    {
        action();
    }
    catch (UnsupportedStatutoryScenarioException)
    {
        assertions++;
        return;
    }

    throw new InvalidOperationException(message);
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
    0m, 30000m, 0.19m, 5700m, 0m, 5700m);
