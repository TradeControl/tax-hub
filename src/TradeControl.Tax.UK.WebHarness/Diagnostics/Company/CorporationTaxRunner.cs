using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed record CorporationTaxRunResult(
    PreparedCompanyAccounts Accounts,
    IReadOnlyList<PreparedCorporationTaxReturn> Returns);

public sealed class CorporationTaxRunner(ConnectionFactory connectionFactory)
{
    public async Task<CorporationTaxRunResult> PrepareAsync(
        CorporationTaxPayload payload,
        CancellationToken cancellationToken = default)
    {
        var accountsPeriod = payload.AccountsPeriod!;
        var reviewed = new CorporationTaxReviewedInput(
            payload.Identity.CompanyNumber,
            payload.Identity.Utr,
            payload.Periods.Select(value => new CorporationTaxPeriodReviewedInput(
                value.Period,
                value.OtherAddBacks,
                value.Deductions,
                value.CapitalAllowances,
                value.LossesUsed,
                value.ChargeableGains,
                value.OtherReliefs,
                value.LoansToParticipators)).ToArray(),
            payload.Declaration.DeclarantName,
            payload.Declaration.DeclarationDate);
        ICorporationTaxSource sourceReader = new TcCorporationTaxSourceReader(connectionFactory, payload.SqlConnection);
        var source = await sourceReader.ReadAsync(new(
            accountsPeriod.End,
            payload.AsOfDate!.Value,
            accountsPeriod,
            reviewed), cancellationToken);
        var populated = new CorporationTaxPopulator().Populate(source);

        var accountsReview = payload.Accounts;
        var accountsRequest = new CompanyProjectionRequest(
            payload.AsOfDate.Value,
            accountsPeriod,
            accountsReview.ComparativePeriod,
            accountsReview.IsFirstAccountsPeriod,
            new(
                payload.Identity.CompanyNumber,
                accountsReview.MembersHaveNotRequiredAudit,
                accountsReview.DirectorsAcknowledgeResponsibilities,
                source.CorporationTaxPeriods.Sum(value => value.CorporationTaxChargeable.Value),
                accountsReview.ComparativeTaxOnProfit,
                accountsReview.PrepaymentsAndAccruedIncome,
                accountsReview.ComparativePrepaymentsAndAccruedIncome,
                accountsReview.Provisions,
                accountsReview.ComparativeProvisions,
                accountsReview.AccrualsAndDeferredIncome,
                accountsReview.ComparativeAccrualsAndDeferredIncome,
                accountsReview.PrincipalActivity,
                accountsReview.AccountingPolicies,
                accountsReview.AverageEmployees,
                accountsReview.DirectorAdvances,
                accountsReview.CommitmentsAndContingencies,
                accountsReview.ApprovedOn,
                accountsReview.SigningDirectorCode,
                accountsReview.SigningDirectorName));
        ICompanyStatutorySource accountsSourceReader = new TcCompanyStatutorySourceReader(connectionFactory, payload.SqlConnection);
        var accountsSource = await accountsSourceReader.ReadAsync(accountsRequest, cancellationToken);
        var accountsContract = new CompanyAccountsPopulator().Populate(accountsSource, new(true, true));
        var preparedAccounts = new CompanyAccountsPreparer().Prepare(
            accountsContract,
            false,
            $"{accountsSource.Identity.SubjectName} statutory accounts",
            $"{accountsSource.Identity.SubjectCode}-full-accounts.xhtml",
            accountsSource.Versions);

        var preparer = new CorporationTaxPreparer();
        var returns = populated.Select((value, index) => preparer.Prepare(
            value,
            preparedAccounts,
            populated.Count == 1 ? payload.CorrelationId : $"{payload.CorrelationId}-{index + 1}",
            source.Versions)).ToArray();
        return new(preparedAccounts, returns);
    }
}
