using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026;

namespace TradeControl.Tax.UK.Hmrc.CorporationTax.Submission.V2026;

public sealed record CorporationTaxReturnPackage(
    Ct600Return Return,
    CorporationTaxComputation Computation,
    DocumentArtifact AccountsDocument,
    DocumentArtifact ComputationDocument,
    IReadOnlyList<DocumentArtifact> SupportingAttachments);

public enum HmrcSubmissionState { Received, Accepted, Rejected }

public sealed record HmrcCorporationTaxAcknowledgement(
    string CorrelationId,
    HmrcSubmissionState State,
    IReadOnlyList<HmrcCorporationTaxError> Errors);

public sealed record HmrcCorporationTaxError(string Code, string Message, string? Location = null);

public static class CorporationTaxEndpointSet
{
    public static SubmissionServiceDescriptor Submit { get; } = new(
        "HMRC", "SubmitCorporationTaxReturn", "Transaction Engine XML", "application/xml", "application/xml",
        "GovernmentGateway", "CT600-V3-2026-RIM-1.994", ContractStatus.Production, false);
}
