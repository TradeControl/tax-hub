namespace TradeControl.Tax.UK.Company.ContractInfrastructure;

public enum CompanyServiceDisposition
{
    Supported,
    Deferred,
    Unsupported
}

public sealed record CompanyServiceCoverage(
    string Authority,
    string Operation,
    string ContractId,
    string ContractVersion,
    CompanyServiceDisposition Disposition,
    string Rationale);

public static class CompanyServiceCoverageCatalog
{
    public static IReadOnlyList<CompanyServiceCoverage> Services { get; } =
    [
        new("Companies House", "Prepare statutory accounts iXBRL", "frc-accounts-taxonomy", "FRC-2026-v1.0.0",
            CompanyServiceDisposition.Supported, "Full and filleted micro-entity preview documents are deterministic and validated against the supported contract catalogue."),
        new("Companies House", "File company accounts", "companies-house-accounts", "TIS-5.9",
            CompanyServiceDisposition.Deferred, "The logical preview package is supported; live filing awaits the separately referenced official Filing TIS envelope schemas."),
        new("Companies House", "Poll filing status", "companies-house-accounts", "TIS-5.9",
            CompanyServiceDisposition.Deferred, "Polling semantics are prepared for Objective 4; no authority transport is implemented in Objective 3."),
        new("Companies House", "Replacement filing API", "companies-house-accounts", "future-api",
            CompanyServiceDisposition.Unsupported, "Final official specifications are unavailable; explicit preview opt-in is diagnostic only."),
        new("HMRC", "Submit Corporation Tax return", "hmrc-ct600", "1.994",
            CompanyServiceDisposition.Deferred, "The CT600 envelope is supported for preview; live submission is blocked by the computation-taxonomy validation-assets deferral."),
        new("HMRC", "Attach Corporation Tax computation", "hmrc-computation-taxonomy", "2025",
            CompanyServiceDisposition.Deferred, "Official offline validation taxonomy assets are not provisioned."),
        new("HMRC", "CT600A loans to participators", "hmrc-ct600", "1.994",
            CompanyServiceDisposition.Supported, "The supplementary page is emitted only when its typed schedule is applicable and valid.")
    ];
}
