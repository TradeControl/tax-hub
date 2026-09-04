namespace TradeControl.Tax.UK.Company.ContractInfrastructure;

public sealed record VersionedContract(
    string ContractId,
    string Version,
    DateOnly EffectiveFrom,
    DateOnly? EffectiveTo,
    ContractStatus Status,
    bool SubmissionReady,
    string? Limitation = null);

public static class CompanyContractRegistry
{
    public static VersionedContract CompaniesHouseAccountsTis59 { get; } = new(
        "companies-house-accounts", "TIS-5.9", new DateOnly(2026, 4, 1), null,
        ContractStatus.Production, false,
        "TIS 5.9 defines the accounts document rules, but the separately referenced Filing TIS envelope schemas are not provisioned in this repository.");

    public static VersionedContract CompaniesHouseReplacement { get; } = new(
        "companies-house-accounts", "future-api", new DateOnly(2028, 1, 1), null,
        ContractStatus.Preview, false, "Final official specifications are not available.");

    public static VersionedContract HmrcCt600V1994 { get; } = new(
        "hmrc-ct600", "1.994", new DateOnly(2026, 4, 7), null,
        ContractStatus.Production, true);

    public static VersionedContract HmrcComputationTaxonomy2025 { get; } = new(
        "hmrc-computation-taxonomy", "2025", new DateOnly(2026, 4, 1), null,
        ContractStatus.Production, false,
        "Accepted by HMRC, but the official validation taxonomy bundle is not provisioned; derived QNames must not be submitted.");

    public static VersionedContract SelectProduction(string contractId, DateOnly on)
    {
        var match = All.SingleOrDefault(x => x.ContractId == contractId && x.Status == ContractStatus.Production &&
            x.EffectiveFrom <= on && (x.EffectiveTo is null || on <= x.EffectiveTo));
        return match ?? throw new UnsupportedStatutoryScenarioException($"No production {contractId} contract is effective on {on:yyyy-MM-dd}.");
    }

    public static IReadOnlyList<VersionedContract> All { get; } =
        [CompaniesHouseAccountsTis59, CompaniesHouseReplacement, HmrcCt600V1994, HmrcComputationTaxonomy2025];
}
