namespace TradeControl.Tax.UK.Company.ContractInfrastructure;

public enum ContractStatus { Production, Preview, Retired, Unsupported }

public sealed record SourceProvenance(
    string Id,
    string Title,
    Uri Source,
    string Version,
    DateOnly PublishedOrVersionDate,
    string Sha256,
    string Redistribution);

public sealed record SubmissionServiceDescriptor(
    string Authority,
    string Operation,
    string Protocol,
    string RequestMediaType,
    string ResponseMediaType,
    string AuthenticationScheme,
    string ContractVersion,
    ContractStatus Status,
    bool RequiresStatusPolling);

public static class CompanyContractCatalog
{
    public static SourceProvenance CompaniesHouseTis59 { get; } = new(
        "companies-house-accounts-tis-5.9", "Companies House technical interface specification for accounts",
        new Uri("https://assets.publishing.service.gov.uk/media/69c5421c23fcbcd838a6f78f/Companies_House_technical_interface_specification__TIS__for_accounts_5.9__003_.odt"),
        "5.9", new DateOnly(2026, 4, 1), "CB92AA331CCC9913B6E1B1AC0FD600120EA30AF1D0B2994078E43CAAB61F3DAF", "Provenance only");

    public static SourceProvenance Ct600V1994 { get; } = new(
        "hmrc-ct600-v3-2026-v1.994", "CT600 V3 (2026) Artefacts",
        new Uri("https://assets.publishing.service.gov.uk/media/68e8a35c1c8b2a3b5069080c/HMRC-CT-2014-v1-994.zip"),
        "1.994", new DateOnly(2025, 10, 10), "504C9DC643195BB5B9AB25A86B9CFF59C6312DE36ADE035BE01CA848D0B81BED", "Official XSD subset retained under OGL; full ZIP provenance only");

    public static SourceProvenance Frc2026 { get; } = new(
        "frc-taxonomy-2026-v1.0.0", "2026 FRC Taxonomy Suite",
        new Uri("https://www.frc.org.uk/documents/8907/FRC-2026-Taxonomy-v1.0.0.zip"),
        "1.0.0", new DateOnly(2025, 11, 18), "AE80AE12D9D747AC531150B0051BCD67E7C9ACF44313DA19105B4CC013566462", "Provenance and derived supported-profile catalog only");

    public static IReadOnlyList<SourceProvenance> Sources { get; } = [CompaniesHouseTis59, Ct600V1994, Frc2026];
}
