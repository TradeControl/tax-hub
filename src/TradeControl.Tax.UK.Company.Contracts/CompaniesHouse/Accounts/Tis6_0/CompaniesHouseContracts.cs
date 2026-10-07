using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

public enum CompaniesHouseSubmissionStatusCode { Accept, Reject, Pending, Parked, InternalFailure }
public enum CompaniesHouseStatusQueryScope { Submission, Company, Presenter }

public sealed record RegistrarStatements(bool AccountsPreparedInAccordanceWithMicroEntityProvisions,
    bool MembersHaveNotRequiredAudit, bool DirectorsAcknowledgeResponsibilities);

public sealed record CompaniesHouseAccountsFiling(StatutoryAccounts Accounts,
    RegistrarStatements Statements, DocumentArtifact AccountsDocument);

/// <summary>A credential-free, non-dispatchable representation of FormSubmission v2.11.</summary>
public sealed record CompaniesHouseFilingPackage(string SubmissionNumber, string PackageReference,
    CompaniesHouseAccountsFiling Filing);

public sealed record CompaniesHouseGatewayAcknowledgement(string? TransactionId,
    DateTimeOffset? GatewayTimestamp, TimeSpan? SuggestedPollInterval,
    IReadOnlyList<CompaniesHouseError> Errors);

public sealed record CompaniesHouseSubmissionStatusRequest(string PresenterId,
    CompaniesHouseStatusQueryScope Scope, string? SubmissionNumber = null, string? CompanyNumber = null)
{
    public bool RequiresStatusAcknowledgement => Scope != CompaniesHouseStatusQueryScope.Submission;
}

public sealed record CompaniesHouseSubmissionStatus(string SubmissionNumber,
    CompaniesHouseSubmissionStatusCode StatusCode, string? CompanyNumber, string? CustomerReference,
    string? ExaminerTelephone, string? ExaminerComment,
    IReadOnlyList<CompaniesHouseError> Errors);

public sealed record CompaniesHouseStatusAcknowledgement;
public sealed record CompaniesHouseError(string Code, string Message, string? Location = null);

public static class CompaniesHouseEndpointSet
{
    public static SubmissionServiceDescriptor SubmitAccounts { get; } = new(
        "CompaniesHouse", "Accounts", "GovTalk XML Gateway / FormSubmission v2.11", "application/xml", "application/xml",
        "PresenterAndCompanyAuthentication", "TIS-6.0", ContractStatus.Production, true);
    public static SubmissionServiceDescriptor GetSubmissionStatus { get; } = new(
        "CompaniesHouse", "GetSubmissionStatus", "GovTalk XML Gateway / GetSubmissionStatus v2.9", "application/xml", "application/xml",
        "PresenterAuthentication", "TIS-6.0", ContractStatus.Production, true);
    public static SubmissionServiceDescriptor StatusAck { get; } = new(
        "CompaniesHouse", "StatusAck", "GovTalk XML Gateway / GetStatusAck v1.1", "application/xml", "application/xml",
        "PresenterAuthentication", "TIS-6.0", ContractStatus.Production, false);
}
