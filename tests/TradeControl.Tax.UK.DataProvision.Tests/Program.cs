using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;
using TradeControl.Tax.UK.Application.DataProvision;

CompanySourceBoundaryTests.Run();
CompanyAccountsPopulationTests.Run();
PreparedArtifactTests.Run();

if (args.Contains("--offline", StringComparer.OrdinalIgnoreCase))
{
    Console.WriteLine("CO1 source-boundary and CO2 prepared-artifact verification passed (offline).");
    return;
}

var connectionString = Environment.GetEnvironmentVariable("TC_NODE_CONTEXT");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("TC_NODE_CONTEXT must be supplied by the local secret-backed test runner.");

var allowedDatabases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "tcNodeDb4-COMIPFVT1-COMIN26",
    "tcNodeDb4-COSIPFVT1-COSTD26",
    "tcNodeDb4-STMIPFVT1-STMIN26",
    "tcNodeDb4-STSIPFVT1-STSTD26"
};

var factory = new ConnectionFactory();
using (var connection = factory.Create(connectionString))
{
    await connection.OpenAsync();
    using var command = new SqlCommand("SELECT DB_NAME();", connection);
    var database = Convert.ToString(await command.ExecuteScalarAsync());
    Assert(database is not null && allowedDatabases.Contains(database),
        "The integration test connection does not target an approved Tax Hub sandbox.");
}

var source = new TcStatutoryContextReader(factory, connectionString);
var snapshot = await source.ReadAsync(DateOnly.FromDateTime(DateTime.Today));
var findings = StatutoryContextVerifier.Verify(snapshot);
Assert(findings.Count == 0,
    "Statutory context findings: " + string.Join(", ", findings.Select(item => item.Code)));

Assert(snapshot.BusinessTaxWindow.Start < snapshot.BusinessTaxWindow.End,
    "The business-tax reporting window is invalid.");
Assert(snapshot.BusinessTaxWindow.End == snapshot.BusinessTaxWindow.Start.AddYears(1).AddDays(-1),
    "The exclusive SQL PayTo boundary was not converted to an inclusive statutory period end.");
Assert(snapshot.Registrations.Any(item => item.SchemeCode == "GB-UTR" && item.DisplayValue.Contains('*')),
    "A masked UTR was not returned.");
Assert(snapshot.Profiles.All(item => string.IsNullOrEmpty(item.AuthorityReferenceDisplay)
    || item.AuthorityReferenceDisplay.Contains('*')),
    "An authority business reference was returned without masking.");

var deliberatelyUnsafe = snapshot with
{
    Registrations = [new("TEST-SENSITIVE", "VISIBLE123", true, true, "SYNTHETIC",
        new("Test", "01", null))]
};
Assert(StatutoryContextVerifier.Verify(deliberatelyUnsafe)
        .Any(item => item.Code == "SENSITIVE-IDENTIFIER-UNMASKED"),
    "Application verification did not reject an unmasked sensitive identifier.");

var unsafeSetting = snapshot with
{
    Settings = [new("TEST", "TEST-SENSITIVE", "TEXT", "VISIBLE123", true, true,
        "SYNTHETIC", new("Test", "01", null))]
};
Assert(StatutoryContextVerifier.Verify(unsafeSetting)
        .Any(item => item.Code == "SENSITIVE-SETTING-UNMASKED"),
    "Application verification did not reject an unmasked sensitive setting.");

if (snapshot.Identity.BusinessTaxTypeCode == 0)
{
    Assert(!string.IsNullOrWhiteSpace(snapshot.Identity.CompanyNumber), "The company number is missing.");
    Assert(snapshot.Profiles.Any(item => item.ReportingTypeCode == "COMPANY-TAX"),
        "The Corporation Tax profile is missing.");
    Assert(snapshot.Profiles.Any(item => item.ReportingTypeCode == "STATUTORY-ACCOUNTS"),
        "The statutory accounts profile is missing.");

    var defaults = CompanyAccountsDraftDefaults.Create(snapshot);
    Assert(defaults.PrincipalActivity.SourceCode == "Subject.tbVirtual.BusinessDescription",
        "Principal activity has an unexpected owner.");
    Assert(defaults.AccountingPolicies.Value.Length > 0, "The accounting policies suggestion is missing.");
    Assert(defaults.AverageEmployees.Origin == SuggestedValueOrigin.Default,
        "Average employees must remain an editable suggestion.");
    Assert(defaults.ComparativePeriod.Value.Start == defaults.Period.Value.Start.AddYears(-1),
        "The comparative period does not use the preceding accounting horizon.");
    Assert(defaults.DirectorAdvances.Value.Count == 0 && defaults.CommitmentsAndContingencies.Value.Count == 0,
        "Absent optional schedules must default to zero.");
    Assert(defaults.Period.Override(defaults.Period.Value with
        { Start = defaults.Period.Value.Start.AddDays(1) }).Origin == SuggestedValueOrigin.OperatorOverride,
        "A submission period override was not recorded as an operator value.");

    var taxDefaults = CorporationTaxDraftDefaults.Create(snapshot);
    Assert(taxDefaults.OtherAddBacks.Value.Count == 0
        && taxDefaults.Deductions.Value.Count == 0
        && taxDefaults.CapitalAllowances.Value == new CapitalAllowanceDraft(0m, 0m, 0m)
        && taxDefaults.LossRelief.Value == new LossReliefDraft(0m, 0m, 0m, 0m)
        && taxDefaults.OtherReliefs.Value == 0m,
        "Absent Corporation Tax schedules must default to zero.");
}
else if (snapshot.Identity.BusinessTaxTypeCode == 4)
{
    Assert(snapshot.Registrations.Any(item => item.SchemeCode == "GB-NI" && item.DisplayValue.Contains('*')),
        "A masked NINO was not returned.");
    Assert(snapshot.Profiles.Any(item => item.ReportingTypeCode == "SELF-EMPLOYMENT"
        && item.TaxSourceCode == "UK-ITSA-SE-CUM"),
        "The self-employment reporting profile is missing or points to the wrong Tax Source.");
    Assert(snapshot.Settings.Any(item => item.SettingCode == "ACCOUNTING-BASIS"),
        "The accounting-basis suggestion is missing.");
}
else
{
    throw new InvalidOperationException("The sandbox has an unsupported business-tax type.");
}

Console.WriteLine("DP5 context, CO1 source boundary and CO2 prepared-artifact verification passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}
