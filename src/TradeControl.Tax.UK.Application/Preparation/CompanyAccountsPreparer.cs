using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Projection;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Validation;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record PreparedCompanyAccounts(
    string Profile,
    string FileName,
    string TaxonomyRelease,
    int FactCount,
    PreparedStatutoryArtifact Artifact);

public sealed class CompanyAccountsPreparer
{
    public PreparedCompanyAccounts Prepare(
        StatutoryAccounts accounts,
        bool filleted,
        string title,
        string fileName,
        IEnumerable<SourceVersion>? sourceEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        var validator = new CompanyContractValidator();
        var sourceFindings = validator.Validate(accounts).Findings;
        var report = new StatutoryAccountsProjection().Project(accounts, filleted, title);
        var reportFindings = validator.Validate(report).Findings;
        var document = new IxbrlDocumentBuilder().Build(report, fileName);
        var findings = sourceFindings.Concat(reportFindings).Select(finding => new PreparedArtifactFinding(
            finding.Severity == ValidationSeverity.Error
                ? PreparedFindingSeverity.Error
                : PreparedFindingSeverity.Warning,
            finding.Code,
            finding.Message,
            finding.Path));
        var artifact = PreparedStatutoryArtifact.Create(
            "GB",
            "COMPANIES-HOUSE",
            "STATUTORY-ACCOUNTS",
            CompanyContractCatalog.Frc2026.Version,
            PreparedArtifactStatus.Preview,
            document.MediaType,
            document.Content,
            sourceEvidence,
            findings);

        return new(
            filleted ? "filleted" : "full",
            document.FileName,
            report.Taxonomy.ReleaseId,
            report.Facts.Count,
            artifact);
    }
}
