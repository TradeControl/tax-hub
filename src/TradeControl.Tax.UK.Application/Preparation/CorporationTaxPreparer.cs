using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Projection;
using TradeControl.Tax.UK.Company.Validation;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Submission.V2026;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record PreparedCorporationTaxReturn(
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    PreparedSubmissionPackage Package);

public sealed class CorporationTaxPreparer
{
    public PreparedCorporationTaxReturn Prepare(
        PopulatedCorporationTaxReturn populated,
        PreparedCompanyAccounts accounts,
        string correlationId,
        IEnumerable<SourceVersion>? sourceEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(populated);
        ArgumentNullException.ThrowIfNull(accounts);
        if (accounts.Artifact.HasErrors)
            throw new InvalidOperationException("Corporation Tax preparation requires valid statutory accounts.");

        var validator = new CompanyContractValidator();
        var computationReport = new CorporationTaxComputationProjection().Project(
            populated.Computation, populated.Return.CompanyRegistrationNumber);
        var computationFindings = validator.Validate(computationReport).Findings;
        var computationDocument = new IxbrlDocumentBuilder().Build(computationReport,
            $"{populated.Return.CompanyRegistrationNumber}-{populated.Return.Period.End:yyyyMMdd}-computation.xhtml");
        var accountsDocument = new DocumentArtifact(accounts.FileName, accounts.Artifact.MediaType,
            accounts.Artifact.Content.ToArray(), accounts.Artifact.Sha256);
        var package = new CorporationTaxReturnPackage(
            populated.Return, populated.Computation, accountsDocument, computationDocument, []);
        var packageFindings = validator.Validate(package).Findings;
        var transmission = new CorporationTaxPackageSerializer().Serialize(package, correlationId);

        var computationArtifact = PreparedStatutoryArtifact.Create(
            "GB", "HMRC", "CORPORATION-TAX-COMPUTATION",
            CompanyContractRegistry.HmrcComputationTaxonomy2025.Version,
            PreparedArtifactStatus.Preview,
            computationDocument.MediaType,
            computationDocument.Content,
            sourceEvidence,
            Findings(computationFindings));
        var transmissionArtifact = PreparedStatutoryArtifact.Create(
            "GB", "HMRC", "SUBMIT-CORPORATION-TAX-RETURN",
            CompanyContractRegistry.HmrcCt600V1994.Version,
            PreparedArtifactStatus.Preview,
            transmission.MediaType,
            transmission.Content,
            sourceEvidence,
            Findings(packageFindings));
        var preparedAccounts = PreparedStatutoryArtifact.Create(
            accounts.Artifact.JurisdictionCode,
            "HMRC",
            accounts.Artifact.OperationCode,
            accounts.Artifact.ContractVersion,
            PreparedArtifactStatus.Preview,
            accounts.Artifact.MediaType,
            accounts.Artifact.Content.AsSpan(),
            accounts.Artifact.SourceEvidence,
            accounts.Artifact.Findings);

        return new(
            populated.Return.Period.Start,
            populated.Return.Period.End,
            new PreparedSubmissionPackage(
                "HMRC-CORPORATION-TAX",
                transmissionArtifact,
                [
                    new(accounts.FileName, preparedAccounts),
                    new(computationDocument.FileName, computationArtifact)
                ],
                new(SubmissionPollingMode.None)));
    }

    private static IEnumerable<PreparedArtifactFinding> Findings(IEnumerable<ValidationFinding> findings) =>
        findings.Select(finding => new PreparedArtifactFinding(
            finding.Severity == ValidationSeverity.Error
                ? PreparedFindingSeverity.Error
                : PreparedFindingSeverity.Warning,
            finding.Code,
            finding.Message,
            finding.Path));
}
