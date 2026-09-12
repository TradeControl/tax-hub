using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CompanyAccountsRunner(ConnectionFactory connectionFactory)
{
    public async Task<PreparedCompanyAccounts> PrepareAsync(
        CompanyAccountsPayload payload,
        CancellationToken cancellationToken = default)
    {
        var review = payload.Review;
        var request = new CompanyProjectionRequest(
            payload.AsOfDate ?? DateOnly.FromDateTime(DateTime.Today),
            payload.Period,
            payload.ComparativePeriod,
            payload.IsFirstAccountsPeriod,
            new(
                review.CompanyNumber,
                review.MembersHaveNotRequiredAudit,
                review.DirectorsAcknowledgeResponsibilities,
                review.TaxOnProfit,
                review.ComparativeTaxOnProfit,
                review.PrepaymentsAndAccruedIncome,
                review.ComparativePrepaymentsAndAccruedIncome,
                review.Provisions,
                review.ComparativeProvisions,
                review.AccrualsAndDeferredIncome,
                review.ComparativeAccrualsAndDeferredIncome,
                review.PrincipalActivity,
                review.AccountingPolicies,
                review.AverageEmployees,
                review.DirectorAdvances,
                review.CommitmentsAndContingencies,
                review.ApprovedOn,
                review.SigningDirectorCode,
                review.SigningDirectorName));
        ICompanyStatutorySource sourceReader = new TcCompanyStatutorySourceReader(connectionFactory, payload.SqlConnection);
        var source = await sourceReader.ReadAsync(request, cancellationToken);
        var accounts = new CompanyAccountsPopulator().Populate(source, new(true, true));
        var filleted = payload.Profile == "filleted";
        return new CompanyAccountsPreparer().Prepare(
            accounts,
            filleted,
            $"{source.Identity.SubjectName} statutory accounts",
            $"{source.Identity.SubjectCode}-{payload.Profile}-accounts.xhtml",
            source.Versions);
    }
}
