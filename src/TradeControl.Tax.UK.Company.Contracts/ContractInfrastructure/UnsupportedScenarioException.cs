namespace TradeControl.Tax.UK.Company.ContractInfrastructure;

public sealed class UnsupportedStatutoryScenarioException : InvalidOperationException
{
    public UnsupportedStatutoryScenarioException(string scenario)
        : base($"The statutory scenario '{scenario}' is not supported by the ordinary UK private micro-company profile.")
    {
        Scenario = scenario;
    }

    public string Scenario { get; }
}

public static class SupportedCompanyProfile
{
    public static void Require(bool condition, string unsupportedScenario)
    {
        if (!condition) throw new UnsupportedStatutoryScenarioException(unsupportedScenario);
    }
}
