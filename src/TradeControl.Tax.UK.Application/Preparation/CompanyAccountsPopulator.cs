using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Company.Statutory;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed class CompanyAccountsPopulator
{
    public StatutoryAccounts Populate(
        CompanyStatutorySource source,
        CompanySourceScenario scenario)
    {
        ArgumentNullException.ThrowIfNull(source);
        CompanySourceSupport.RequireOrdinaryPrivateMicroCompany(scenario);

        Require(source.Identity.BusinessTaxTypeCode == 0, "The statutory source is not a company.");
        Require(!string.IsNullOrWhiteSpace(source.Identity.CompanyNumber), "The company number is missing.");
        Require(source.Periods.Current.Start <= source.Periods.Current.End, "The accounts period is invalid.");
        Require(!string.IsNullOrWhiteSpace(source.Approval.SigningDirectorName.Value), "A signing director is required.");
        Require(source.Approval.ApprovedOn.State == StatutoryValueState.ReviewedFilingInput,
            "The accounts approval date must be reviewed for this filing.");
        Require(source.Approval.SigningDirectorName.State == StatutoryValueState.ReviewedFilingInput,
            "The signing director must be reviewed for this filing.");

        var comparative = source.Periods.Comparative;
        if (!source.Periods.IsFirstAccountsPeriod.Value)
            Require(comparative is not null, "Comparative accounts are required unless this is the first accounts period.");

        return new(
            new CompanyIdentity(
                source.Identity.SubjectName,
                source.Identity.CompanyNumber!,
                source.Identity.RegistryJurisdictionCode),
            Period(source.Periods.Current),
            comparative is null ? null : Period(comparative),
            new AccountsProfile(
                Map(source.Profile.Framework),
                Map(source.Profile.AccountsType),
                Map(source.Profile.AuditStatus),
                source.Profile.MembersHaveNotRequiredAudit.Value,
                source.Profile.DirectorsAcknowledgeResponsibilities.Value),
            new StatementOfFinancialPosition(
                Amount(source.BalanceSheet.FixedAssets),
                Amount(source.BalanceSheet.CurrentAssets),
                Amount(source.BalanceSheet.PrepaymentsAndAccruedIncome),
                Amount(source.BalanceSheet.CreditorsDueWithinOneYear),
                Amount(source.BalanceSheet.NetCurrentAssetsLiabilities),
                Amount(source.BalanceSheet.TotalAssetsLessCurrentLiabilities),
                Amount(source.BalanceSheet.CreditorsDueAfterOneYear),
                Amount(source.BalanceSheet.Provisions),
                Amount(source.BalanceSheet.AccrualsAndDeferredIncome),
                Amount(source.BalanceSheet.NetAssetsLiabilities),
                Amount(source.BalanceSheet.CapitalAndReserves)),
            new IncomeStatement(
                Amount(source.IncomeStatement.Turnover),
                Amount(source.IncomeStatement.OtherIncome),
                Amount(source.IncomeStatement.CostOfSales),
                Amount(source.IncomeStatement.AdministrativeExpenses),
                Amount(source.IncomeStatement.TaxOnProfit),
                Amount(source.IncomeStatement.ProfitLossForPeriod)),
            new AccountsNotes(
                source.Notes.PrincipalActivity.Value,
                source.Notes.AccountingPolicies.Value,
                source.Notes.AverageEmployees.Value,
                source.Notes.DirectorAdvances.Select(value => new DirectorAdvance(
                    value.DirectorName, value.OpeningBalance.Value, value.Advances.Value,
                    value.Repayments.Value, value.ClosingBalance.Value, value.Terms.Value)).ToArray(),
                source.Notes.CommitmentsAndContingencies.Select(value => new CommitmentOrContingency(
                    value.Description.Value, value.Amount.Value)).ToArray()),
            new AccountsApproval(source.Approval.ApprovedOn.Value, source.Approval.SigningDirectorName.Value),
            source.Identity.CurrencyCode);
    }

    private static ReportingPeriod Period(ReportingWindow value) => new(value.Start, value.End);

    private static ComparativeAmount Amount(ComparativeStatutoryAmount value) =>
        new(value.Current.Value, value.Comparative?.Value);

    private static ReportingFramework Map(CompanyReportingFramework value) => value switch
    {
        CompanyReportingFramework.Frs105 => ReportingFramework.Frs105,
        _ => throw new InvalidOperationException($"Unsupported reporting framework '{value}'.")
    };

    private static AccountsType Map(CompanyAccountsType value) => value switch
    {
        CompanyAccountsType.MicroEntity => AccountsType.MicroEntity,
        _ => throw new InvalidOperationException($"Unsupported accounts type '{value}'.")
    };

    private static AuditStatus Map(CompanyAuditStatus value) => value switch
    {
        CompanyAuditStatus.UnauditedExempt => AuditStatus.UnauditedExempt,
        CompanyAuditStatus.Audited => AuditStatus.Audited,
        _ => throw new InvalidOperationException($"Unsupported audit status '{value}'.")
    };

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
