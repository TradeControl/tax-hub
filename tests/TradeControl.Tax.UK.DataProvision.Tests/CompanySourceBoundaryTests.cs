using TradeControl.Tax.UK.Application.DataProvision;

internal static class CompanySourceBoundaryTests
{
    public static void Run()
    {
        CompanySourceSupport.RequireOrdinaryPrivateMicroCompany(new(true, true));

        AssertRejected(new(false, true), "non-private-company");
        AssertRejected(new(true, false), "non-micro-entity");
        AssertRejected(new(true, true, RequiresGroupAccounts: true), "group-accounts");
        AssertRejected(new(true, true, HasMultipleTrades: true), "multiple-trades");
        AssertRejected(new(true, true, HasSpecialistActivities: true), "specialist-activities");
        AssertRejected(new(true, true, RequiresUnsupportedSupplementaryReturn: true),
            "unsupported-supplementary-return");

        var explicitZero = new StatutorySourceValue<decimal>(
            0m, StatutoryValueState.ExplicitZero, "SubmissionDefault", []);
        if (explicitZero.State != StatutoryValueState.ExplicitZero)
            throw new InvalidOperationException("An explicit filing zero lost its value state.");
    }

    private static void AssertRejected(CompanySourceScenario scenario, string expected)
    {
        try
        {
            CompanySourceSupport.RequireOrdinaryPrivateMicroCompany(scenario);
        }
        catch (UnsupportedCompanySourceScenarioException exception) when (exception.Scenario == expected)
        {
            return;
        }

        throw new InvalidOperationException($"The unsupported company scenario '{expected}' did not fail closed.");
    }
}
