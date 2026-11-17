using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;

internal static class CorporationTaxPopulationTests
{
    public static void Run()
    {
        var source = Fixture();
        var populated = new CorporationTaxPopulator().Populate(source).Single();
        Assert(populated.Computation.Liability.CalculationCode == "MARGINAL-RELIEF"
            && populated.Computation.Liability.RelevantDistributions == 10000m
            && populated.Computation.Liability.AugmentedProfits == 110000m,
            "The reviewed marginal-relief facts were not carried into the computation.");

        var period = source.CorporationTaxPeriods.Single();
        var provisional = period with
        {
            Liability = period.Liability with
            {
                Value = period.Liability.Value with
                {
                    RateFindingCode = "CT-RATE-PROVISIONAL",
                    IsFilingReady = false
                }
            }
        };
        try
        {
            _ = new CorporationTaxPopulator().Populate(source with
            {
                CorporationTaxPeriods = [provisional]
            });
            throw new InvalidOperationException("A provisional rate reached CT artifact preparation.");
        }
        catch (InvalidOperationException exception) when (
            exception.Message.Contains("CT-RATE-PROVISIONAL", StringComparison.Ordinal))
        {
        }
    }

    private static CorporationTaxSource Fixture()
    {
        var period = new ReportingWindow(new(2025, 7, 1), new(2026, 6, 30));
        IReadOnlyList<SourceVersion> versions = [new("Cash.vwTaxCorpLiabilityUK", "01", null)];
        StatutorySourceValue<T> S<T>(T value) =>
            new(value, StatutoryValueState.Source, "TradeControl", versions);
        StatutorySourceValue<T> R<T>(T value) =>
            new(value, StatutoryValueState.ReviewedFilingInput, "Reviewer", []);
        StatutorySourceValue<T> D<T>(T value) =>
            new(value, StatutoryValueState.Derived, "CO4-Computation", versions);
        var liability = new CorporationTaxLiabilitySource(
            "UK", "UK-CO-CT-2026", "CT-ORDINARY",
            10000m, 110000m, 0,
            0.25m, 0.19m, 0.015m, 50000m, 250000m,
            50000m, 250000m, 25000m, 1909.09091m, 23090.90909m,
            0m, 23090.90909m, "MARGINAL-RELIEF",
            null, null, null, true, true);
        var corporationTaxPeriod = new CorporationTaxPeriodSource(
            period,
            S(300000m),
            D(110000m),
            [],
            [S(new TaxAdjustmentDraft("Relevant distributions", 10000m))],
            R(new CapitalAllowanceDraft(0m, 0m, 0m)),
            S(new LossReliefDraft(0m, 0m, 0m, 0m)),
            R(0m),
            D(100000m),
            S(liability),
            S(23090.90909m),
            R(0m),
            D(23090.90909m),
            S(0m),
            S(23090.90909m),
            R<LoansToParticipatorsDraft?>(null));
        return new(
            "TEST", "Example Limited", "01234567", "1234567890", period,
            [corporationTaxPeriod],
            new(R("Jane Director"), R(new DateOnly(2026, 11, 17))),
            versions);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
