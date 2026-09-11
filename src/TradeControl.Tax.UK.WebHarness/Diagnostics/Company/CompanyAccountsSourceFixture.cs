using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

internal static class CompanyAccountsSourceFixture
{
    public static CompanyStatutorySource Create(string template)
    {
        var version = new SourceVersion($"WebHarnessFixture:{template}", "FIXTURE-1", null);
        IReadOnlyList<SourceVersion> versions = [version];
        StatutorySourceValue<T> Source<T>(T value) => new(value, StatutoryValueState.Source, template, versions);
        StatutorySourceValue<T> Reviewed<T>(T value) => new(value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", versions);
        StatutorySourceValue<T> Derived<T>(T value) => new(value, StatutoryValueState.Derived, "CO3-Reconciliation", versions);
        StatutorySourceValue<T> Zero<T>(T value) => new(value, StatutoryValueState.ExplicitZero, "SubmissionDefault", versions);
        ComparativeStatutoryAmount A(decimal current, decimal comparative, bool derived = false) =>
            new(derived ? Derived(current) : Source(current), derived ? Derived(comparative) : Source(comparative));
        ComparativeStatutoryAmount Z() => new(Zero(0m), Zero(0m));

        return new(
            new("TESTCO", "Example Limited", 0, "UK", "EnglandAndWales", "GBP", "01234567", null,
                "Software development", 3, null, "1 Test Way", "1 Test Way", versions),
            new(new(new DateOnly(2025, 7, 1), new DateOnly(2026, 6, 30)),
                new(new DateOnly(2024, 7, 1), new DateOnly(2025, 6, 30)), Source(false)),
            new(CompanyReportingFramework.Frs105, CompanyAccountsType.MicroEntity,
                CompanyAuditStatus.UnauditedExempt, Reviewed(true), Reviewed(true)),
            new(A(12000, 10000), A(35000, 30000), A(1000, 800), A(18000, 16000),
                A(18000, 14800, true), A(30000, 24800, true), A(5000, 4000), Z(), Z(),
                A(25000, 20800, true), A(25000, 20800, true)),
            new(A(120000, 100000), A(1000, 500), A(60000, 50000), A(33000, 29000),
                A(5700, 4085, true), A(22300, 17415, true)),
            new(Reviewed("Software development"), Reviewed("FRS 105 historical cost basis"), Reviewed(3),
                [], []),
            new(Reviewed(new DateOnly(2026, 7, 31)), Reviewed("DIRECTOR-1"), Reviewed("Jane Director")),
            versions);
    }
}
