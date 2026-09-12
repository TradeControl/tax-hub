using Microsoft.Data.SqlClient;
using System.Runtime.InteropServices;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;

NativeTestProcess.SetErrorMode(NativeTestProcess.SemNoGpFaultErrorBox);

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

if (args.Contains("--corporation-tax-profit", StringComparer.OrdinalIgnoreCase))
{
    const decimal expectedTax = 10775.81867m;
    var period = snapshot.BusinessTaxWindow;
    var taxProjection = await new TcBusinessTaxReader(factory).ReadCorporationTaxAsync(
        connectionString,
        period.Start.ToDateTime(TimeOnly.MinValue),
        period.End.AddDays(1).ToDateTime(TimeOnly.MinValue));
    Assert(taxProjection.BusinessTaxRate == 0.19m, "The Corporation Tax source rate is not 19%.");
    Assert(taxProjection.CalculatedTaxDue == expectedTax && taxProjection.StatementTaxDue == expectedTax,
        $"Expected Corporation Tax of {expectedTax:N5}, got {taxProjection.StatementTaxDue:N5}.");
    Assert(taxProjection.LossesCarriedForward == 0m,
        "The profit-making scenario unexpectedly has carried-forward losses.");

    var corporationTaxSource = await new TcCorporationTaxSourceReader(factory, connectionString).ReadAsync(new(
        period.End,
        DateOnly.FromDateTime(DateTime.Today),
        period,
        new(snapshot.Identity.CompanyNumber, "1234567890",
            [new(period, [], [], new(0m, 0m, 0m), 0m, 0m, 0m, null)],
            "Synthetic Director", period.End.AddDays(30))));
    var corporationTaxPeriod = corporationTaxSource.CorporationTaxPeriods.Single();
    Assert(corporationTaxPeriod.CorporationTaxChargeable.Value == expectedTax
        && corporationTaxPeriod.TaxPayable.Value == expectedTax,
        "The profit-making submission does not preserve the statement-backed Corporation Tax liability.");
    Assert(corporationTaxPeriod.LossRelief.Value.BroughtForward == 0m
        && corporationTaxPeriod.LossRelief.Value.CurrentPeriod == 0m
        && corporationTaxPeriod.LossRelief.Value.Used == 0m
        && corporationTaxPeriod.LossRelief.Value.CarriedForward == 0m,
        "The profit-making submission unexpectedly contains a loss movement.");
    await AssertRejectedAsync(() => new TcCorporationTaxSourceReader(factory, connectionString).ReadAsync(new(
        period.End, DateOnly.FromDateTime(DateTime.Today), period,
        new(snapshot.Identity.CompanyNumber, "1234567890",
            [new(period, [new("Unsupported add-back", 1m)], [], new(0m, 0m, 0m), 0m, 0m, 0m, null)],
            "Synthetic Director", period.End.AddDays(30)))),
        "An irreconcilable reviewed add-back was accepted.");
    await AssertRejectedAsync(() => new TcCorporationTaxSourceReader(factory, connectionString).ReadAsync(new(
        period.End, DateOnly.FromDateTime(DateTime.Today), period,
        new(snapshot.Identity.CompanyNumber, "1234567890",
            [new(period, [], [], new(0m, 0m, 0m), 1m, 0m, 0m, null)],
            "Synthetic Director", period.End.AddDays(30)))),
        "An irreconcilable reviewed loss claim was accepted.");
    Console.WriteLine($"CO4 profit scenario passed: tax={expectedTax:N5}, rate={taxProjection.BusinessTaxRate:P0}, losses carried forward=0.00.");
    return;
}

if (args.Contains("--corporation-tax-loss", StringComparer.OrdinalIgnoreCase))
{
    const decimal expectedLossesBroughtForward = 79242.47m;
    const decimal expectedLossesCarriedForward = 103759.93m;
    var period = snapshot.BusinessTaxWindow;
    var taxProjection = await new TcBusinessTaxReader(factory).ReadCorporationTaxAsync(
        connectionString,
        period.Start.ToDateTime(TimeOnly.MinValue),
        period.End.AddDays(1).ToDateTime(TimeOnly.MinValue));
    Console.WriteLine($"CO4 projection: period={period.Start:yyyy-MM-dd}/{period.End:yyyy-MM-dd}, "
        + $"netProfit={taxProjection.NetProfit:N5}, taxDue={taxProjection.StatementTaxDue:N5}, "
        + $"paid={taxProjection.StatementTaxPaid:N5}, balance={taxProjection.StatementBalance:N5}, "
        + $"previousLoss={taxProjection.PreviousLossesCarriedForward:N5}, carriedLoss={taxProjection.LossesCarriedForward:N5}.");
    Assert(taxProjection.CalculatedTaxDue == taxProjection.StatementTaxDue,
        "Calculated Corporation Tax does not reconcile to the statement.");
    Assert(taxProjection.StatementTaxDue <= 0m,
        "The loss-making scenario unexpectedly has positive Corporation Tax.");
    Assert(decimal.Round(taxProjection.LossesCarriedForward, 2) == expectedLossesCarriedForward,
        $"Expected carried-forward losses of {expectedLossesCarriedForward:N2}, got {taxProjection.LossesCarriedForward:N2}.");
    Assert(decimal.Round(taxProjection.PreviousLossesCarriedForward, 2) == expectedLossesBroughtForward,
        $"Expected brought-forward losses of {expectedLossesBroughtForward:N2}, got {taxProjection.PreviousLossesCarriedForward:N2}.");

    var corporationTaxSource = await new TcCorporationTaxSourceReader(factory, connectionString).ReadAsync(new(
        period.End,
        DateOnly.FromDateTime(DateTime.Today),
        period,
        new(snapshot.Identity.CompanyNumber, "1234567890",
            [new(period, [], [], new(0m, 0m, 0m), 0m, 0m, 0m, null)],
            "Synthetic Director", period.End.AddDays(30))));
    var corporationTaxPeriod = corporationTaxSource.CorporationTaxPeriods.Single();
    Assert(corporationTaxPeriod.CorporationTaxChargeable.Value == 0m
        && corporationTaxPeriod.TaxPayable.Value == 0m,
        "The loss-making submission did not produce zero Corporation Tax.");
    Assert(decimal.Round(corporationTaxPeriod.LossRelief.Value.CarriedForward, 2) == expectedLossesCarriedForward,
        "The submission loss schedule does not preserve the statement-derived carried-forward loss.");
    Assert(decimal.Round(corporationTaxPeriod.LossRelief.Value.BroughtForward, 2) == expectedLossesBroughtForward,
        "The submission loss schedule does not preserve the statement-derived brought-forward loss.");
    _ = new CorporationTaxPopulator().Populate(corporationTaxSource).Single();
    _ = await new TcCompanyStatutorySourceReader(factory, connectionString).ReadAsync(new(
        DateOnly.FromDateTime(DateTime.Today), period, null, false,
        new(snapshot.Identity.CompanyNumber, true, true, 0m, null,
            0m, null, 0m, null, 0m, null, null, null, null, [], [],
            period.End.AddDays(30), "DIRECTOR-1", "Synthetic Director")));
    Console.WriteLine($"CO4 loss scenario passed: tax=0.00, losses brought forward={expectedLossesBroughtForward:N2}, losses carried forward={expectedLossesCarriedForward:N2}.");
    return;
}

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

    var companySource = await new TcCompanyStatutorySourceReader(factory, connectionString).ReadAsync(
        new(DateOnly.FromDateTime(DateTime.Today), null, null, false,
            new("12345678", true, true, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m,
                null, null, null, [], [], defaults.Period.Value.End.AddDays(30), "DIRECTOR-1", "Synthetic Director")));
    var projectedIncome = await new TcBusinessTaxReader(factory).ReadCumulativeAsync(
        connectionString, "UK-CO-ACCTS-2026",
        companySource.Periods.Current.Start.ToDateTime(TimeOnly.MinValue),
        companySource.Periods.Current.End.AddDays(1).ToDateTime(TimeOnly.MinValue));
    decimal Projected(string tag) => projectedIncome.Values.Single(item =>
        item.TagCode == tag && item.SupportStatus == TcTaxSupportStatus.Supported).StatutoryAmount!.Value;
    Assert(companySource.IncomeStatement.Turnover.Current.Value == Projected("IncomeStatement.Turnover")
        && companySource.IncomeStatement.OtherIncome.Current.Value == Projected("IncomeStatement.OtherIncome")
        && companySource.IncomeStatement.CostOfSales.Current.Value == Projected("IncomeStatement.CostOfSales")
        && companySource.IncomeStatement.AdministrativeExpenses.Current.Value == Projected("IncomeStatement.AdministrativeExpenses"),
        "The company source did not preserve the SQL projection's exclusive PayTo boundary.");

    var corporationTaxSource = await new TcCorporationTaxSourceReader(factory, connectionString).ReadAsync(new(
        defaults.Period.Value.End,
        DateOnly.FromDateTime(DateTime.Today),
        defaults.Period.Value,
        new("12345678", "1234567890",
            [new(defaults.Period.Value, [], [], new(0m, 0m, 0m),
                0m, 0m, 0m, null)],
            "Synthetic Director", defaults.Period.Value.End.AddDays(30))));
    var corporationTaxPeriod = corporationTaxSource.CorporationTaxPeriods.Single();
    Assert(corporationTaxPeriod.Turnover.Value == companySource.IncomeStatement.Turnover.Current.Value,
        "Corporation Tax turnover does not reconcile to the accounts projection.");
    Assert(corporationTaxPeriod.AccountsProfitLossBeforeTax.Value
        == companySource.IncomeStatement.ProfitLossForPeriod.Current.Value,
        "Corporation Tax accounting profit does not reconcile to the zero-tax accounts source.");
    Assert(corporationTaxPeriod.CorporationTaxChargeable.Value
        == decimal.Round(corporationTaxPeriod.TaxableTotalProfits.Value * corporationTaxPeriod.MainRate.Value, 5, MidpointRounding.AwayFromZero),
        "Corporation Tax chargeable does not reconcile to taxable profits and the source rate.");
    var masterTax = await new TcBusinessTaxReader(factory).ReadCorporationTaxAsync(
        connectionString,
        corporationTaxPeriod.Period.Start.ToDateTime(TimeOnly.MinValue),
        corporationTaxPeriod.Period.End.AddDays(1).ToDateTime(TimeOnly.MinValue));
    Assert(corporationTaxPeriod.MainRate.State == StatutoryValueState.Source
        && corporationTaxPeriod.MainRate.Value == masterTax.BusinessTaxRate,
        "Corporation Tax did not use the App.tbYearPeriod source rate.");
    Assert(corporationTaxPeriod.CorporationTaxChargeable.Value == Math.Max(0m, masterTax.StatementTaxDue)
        && corporationTaxPeriod.TaxPaid.Value == Math.Abs(masterTax.StatementTaxPaid)
        && corporationTaxPeriod.StatementBalance.Value == masterTax.StatementBalance,
        "Corporation Tax liability, payment or balance does not reconcile to Cash.vwTaxBizStatement.");
    Assert(corporationTaxPeriod.LossRelief.Value.CarriedForward == masterTax.LossesCarriedForward,
        "Corporation Tax losses do not reconcile to Cash.vwTaxLossesCarriedForward.");
    var populatedTax = new CorporationTaxPopulator().Populate(corporationTaxSource).Single();
    Assert(populatedTax.Return.TaxPayable == populatedTax.Computation.TaxPayable,
        "The populated CT600 does not reconcile to the computation.");

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

Console.WriteLine("DP5 context and CO1-CO4 source, artifact and reconciliation verification passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static async Task AssertRejectedAsync(Func<Task> action, string message)
{
    try
    {
        await action();
    }
    catch (InvalidOperationException)
    {
        return;
    }

    throw new InvalidOperationException(message);
}

internal static class NativeTestProcess
{
    internal const uint SemNoGpFaultErrorBox = 0x0002;

    [DllImport("kernel32.dll")]
    internal static extern uint SetErrorMode(uint mode);
}
