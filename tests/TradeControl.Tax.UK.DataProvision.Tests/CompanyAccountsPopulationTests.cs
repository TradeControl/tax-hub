using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Company.Validation;

internal static class CompanyAccountsPopulationTests
{
    public static void Run()
    {
        foreach (var template in new[] { "MIN", "STD" })
        {
            var accounts = new CompanyAccountsPopulator().Populate(Fixture(template), new(true, true));
            Assert(new CompanyContractValidator().Validate(accounts).IsValid,
                $"The {template} source did not populate valid statutory accounts.");
            Assert(accounts.BalanceSheet.NetAssetsLiabilities.Current == 25000m,
                $"The {template} current balance sheet changed during population.");
            Assert(accounts.BalanceSheet.NetAssetsLiabilities.Comparative == 20800m,
                $"The {template} comparative balance sheet changed during population.");
            Assert(accounts.IncomeStatement.ProfitLossForPeriod.Current == 22300m,
                $"The {template} income statement changed during population.");
        }

        var unreviewed = Fixture("MIN");
        unreviewed = unreviewed with
        {
            Approval = unreviewed.Approval with
            {
                ApprovedOn = unreviewed.Approval.ApprovedOn with { State = StatutoryValueState.Source }
            }
        };
        AssertRejected(unreviewed, "An unreviewed approval date was accepted.");

        try
        {
            new CompanyAccountsPopulator().Populate(Fixture("STD"), new(true, true, RequiresGroupAccounts: true));
            throw new InvalidOperationException("A group-accounts source was accepted.");
        }
        catch (UnsupportedCompanySourceScenarioException) { }
    }

    private static CompanyStatutorySource Fixture(string template)
    {
        var v = new SourceVersion($"Fixture:{template}", "01", null);
        IReadOnlyList<SourceVersion> versions = [v];
        StatutorySourceValue<T> S<T>(T value) => new(value, StatutoryValueState.Source, template, versions);
        StatutorySourceValue<T> R<T>(T value) => new(value, StatutoryValueState.ReviewedFilingInput, "Reviewer", versions);
        StatutorySourceValue<T> D<T>(T value) => new(value, StatutoryValueState.Derived, "Reconciliation", versions);
        StatutorySourceValue<T> Z<T>(T value) => new(value, StatutoryValueState.ExplicitZero, "SubmissionDefault", versions);
        ComparativeStatutoryAmount A(decimal current, decimal comparative) => new(S(current), S(comparative));
        ComparativeStatutoryAmount AD(decimal current, decimal comparative) => new(D(current), D(comparative));
        ComparativeStatutoryAmount AZ() => new(Z(0m), Z(0m));

        return new(
            new("TEST", "Example Limited", 0, "UK", "EnglandAndWales", "GBP", "01234567", null,
                "Software development", 3, null, "1 Test Way", "1 Test Way", versions),
            new(new(new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30)),
                new(new DateOnly(2024, 7, 1), new DateOnly(2025, 6, 30)), S(false)),
            new(CompanyReportingFramework.Frs105, CompanyAccountsType.MicroEntity,
                CompanyAuditStatus.UnauditedExempt, R(true), R(true)),
            new(A(12000, 10000), A(35000, 30000), A(1000, 800), A(18000, 16000),
                AD(18000, 14800), AD(30000, 24800), A(5000, 4000), AZ(), AZ(),
                AD(25000, 20800), AD(25000, 20800)),
            new(A(120000, 100000), A(1000, 500), A(60000, 50000), A(33000, 29000),
                AD(5700, 4085), AD(22300, 17415)),
            new(R("Software development"), R("FRS 105 historical cost basis"), R(3), [], []),
            new(R(new DateOnly(2026, 7, 31)), R("DIRECTOR-1"), R("Jane Director")), versions);
    }

    private static void AssertRejected(CompanyStatutorySource source, string message)
    {
        try { new CompanyAccountsPopulator().Populate(source, new(true, true)); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
