using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Validation;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis5_9;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed class CompaniesHouseAccountsPreparer
{
    public PreparedSubmissionPackage Prepare(
        TradeControl.Tax.UK.Company.Statutory.StatutoryAccounts accounts,
        PreparedCompanyAccounts preparedAccounts,
        string envelopeNumber,
        RegistrarStatements statements,
        string contractVersion,
        DateOnly contractDate,
        bool allowPreviewContract,
        IEnumerable<SourceVersion>? sourceEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(preparedAccounts);
        ArgumentNullException.ThrowIfNull(statements);
        if (preparedAccounts.Artifact.HasErrors)
            throw new InvalidOperationException("Companies House preparation requires valid statutory accounts.");
        if (preparedAccounts.Profile is not ("full" or "filleted"))
            throw new InvalidOperationException("The prepared accounts delivery profile is unsupported.");

        var contract = CompanyContractRegistry.SelectForPreview(
            "companies-house-accounts", contractVersion, contractDate, allowPreviewContract);

        var delivery = preparedAccounts.Profile == "filleted"
            ? DeliveredAccountsProfile.Filleted
            : DeliveredAccountsProfile.Full;
        var accountsDocument = new DocumentArtifact(preparedAccounts.FileName, preparedAccounts.Artifact.MediaType,
            preparedAccounts.Artifact.Content.ToArray(), preparedAccounts.Artifact.Sha256);
        var filing = new CompaniesHouseFilingPackage(envelopeNumber,
            new(accounts, delivery, statements, accountsDocument));
        var findings = new CompanyContractValidator().Validate(filing).Findings.Select(value =>
            new PreparedArtifactFinding(
                value.Severity == ValidationSeverity.Error ? PreparedFindingSeverity.Error : PreparedFindingSeverity.Warning,
                value.Code, value.Message, value.Path));
        var transmission = new CompaniesHouseEnvelopeSerializer().Serialize(filing);
        var transmissionArtifact = PreparedStatutoryArtifact.Create(
            "GB", "COMPANIES-HOUSE", "FILE-COMPANY-ACCOUNTS", contract.Version,
            PreparedArtifactStatus.Preview, transmission.MediaType, transmission.Content, sourceEvidence, findings);
        var documentArtifact = PreparedStatutoryArtifact.Create(
            preparedAccounts.Artifact.JurisdictionCode, "COMPANIES-HOUSE", "STATUTORY-ACCOUNTS",
            preparedAccounts.Artifact.ContractVersion, PreparedArtifactStatus.Preview,
            preparedAccounts.Artifact.MediaType, preparedAccounts.Artifact.Content.AsSpan(),
            preparedAccounts.Artifact.SourceEvidence, preparedAccounts.Artifact.Findings);

        return new("COMPANIES-HOUSE-ACCOUNTS", transmissionArtifact,
            [new(preparedAccounts.FileName, documentArtifact)],
            new(SubmissionPollingMode.PollUntilTerminal, $"submission-status/{Uri.EscapeDataString(envelopeNumber)}"));
    }
}
