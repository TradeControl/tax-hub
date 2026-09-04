using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis5_9;

public enum DeliveredAccountsProfile { Full, Filleted }
public enum CompaniesHouseSubmissionState { Received, Pending, Accepted, Rejected }

public sealed record RegistrarStatements(
    bool AccountsPreparedInAccordanceWithMicroEntityProvisions,
    bool MembersHaveNotRequiredAudit,
    bool DirectorsAcknowledgeResponsibilities);

public sealed record CompaniesHouseAccountsFiling(
    StatutoryAccounts Accounts,
    DeliveredAccountsProfile Delivery,
    RegistrarStatements Statements,
    DocumentArtifact AccountsDocument);

public sealed record CompaniesHouseFilingPackage(
    string EnvelopeNumber,
    CompaniesHouseAccountsFiling Filing);

public sealed record CompaniesHouseSubmissionAcknowledgement(
    string EnvelopeNumber,
    string SubmissionNumber,
    CompaniesHouseSubmissionState State,
    IReadOnlyList<CompaniesHouseError> Errors);

public sealed record CompaniesHouseError(string Code, string Message, string? Location = null);

public static class CompaniesHouseEndpointSet
{
    public static SubmissionServiceDescriptor SubmitAccounts { get; } = new(
        "CompaniesHouse", "SubmitAccounts", "GovTalk XML Gateway", "application/xml", "application/xml",
        "PresenterAndCompanyAuthentication", "TIS-5.9", ContractStatus.Production, true);

    public static SubmissionServiceDescriptor GetSubmissionStatus { get; } = new(
        "CompaniesHouse", "GetSubmissionStatus", "GovTalk XML Gateway", "application/xml", "application/xml",
        "PresenterAuthentication", "TIS-5.9", ContractStatus.Production, true);
}
