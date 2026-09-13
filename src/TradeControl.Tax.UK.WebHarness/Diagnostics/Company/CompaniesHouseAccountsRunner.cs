using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis5_9;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CompaniesHouseAccountsRunner(ConnectionFactory connectionFactory)
{
    public async Task<PreparedSubmissionPackage> PrepareAsync(CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default)
    {
        var review = payload.Review;
        var request = new CompanyProjectionRequest(
            payload.AsOfDate ?? DateOnly.FromDateTime(DateTime.Today), payload.Period, payload.ComparativePeriod,
            payload.IsFirstAccountsPeriod,
            new(review.CompanyNumber, review.MembersHaveNotRequiredAudit, review.DirectorsAcknowledgeResponsibilities,
                review.TaxOnProfit, review.ComparativeTaxOnProfit, review.PrepaymentsAndAccruedIncome,
                review.ComparativePrepaymentsAndAccruedIncome, review.Provisions, review.ComparativeProvisions,
                review.AccrualsAndDeferredIncome, review.ComparativeAccrualsAndDeferredIncome,
                review.PrincipalActivity, review.AccountingPolicies, review.AverageEmployees,
                review.DirectorAdvances, review.CommitmentsAndContingencies, review.ApprovedOn,
                review.SigningDirectorCode, review.SigningDirectorName));
        ICompanyStatutorySource reader = new TcCompanyStatutorySourceReader(connectionFactory, payload.SqlConnection);
        var source = await reader.ReadAsync(request, cancellationToken);
        var accounts = new CompanyAccountsPopulator().Populate(source, new(true, true));
        var prepared = new CompanyAccountsPreparer().Prepare(accounts, payload.Profile == "filleted",
            $"{source.Identity.SubjectName} statutory accounts",
            $"{source.Identity.SubjectCode}-{payload.Profile}-accounts.xhtml", source.Versions);
        var filing = payload.Filing;
        return new CompaniesHouseAccountsPreparer().Prepare(accounts, prepared, filing.EnvelopeNumber,
            new RegistrarStatements(filing.AccountsPreparedInAccordanceWithMicroEntityProvisions,
                filing.MembersHaveNotRequiredAudit, filing.DirectorsAcknowledgeResponsibilities),
            filing.ContractVersion, payload.AsOfDate ?? DateOnly.FromDateTime(DateTime.Today),
            filing.AllowPreviewContract, source.Versions);
    }
}
