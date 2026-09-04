namespace TradeControl.Tax.UK.Company.Statutory;

public sealed record CompanyIdentity(
    string CompanyName,
    string CompanyNumber,
    string Jurisdiction = "EnglandAndWales");

public sealed record ReportingPeriod(DateOnly Start, DateOnly End)
{
    public int InclusiveDays => End.DayNumber - Start.DayNumber + 1;
}

public sealed record AccountsApproval(DateOnly ApprovedOn, string SigningDirectorName);

public enum ReportingFramework { Frs105 }
public enum AccountsType { MicroEntity }
public enum AuditStatus { UnauditedExempt, Audited }

public sealed record AccountsProfile(
    ReportingFramework Framework,
    AccountsType Type,
    AuditStatus AuditStatus,
    bool MembersHaveNotRequiredAudit,
    bool DirectorsAcknowledgeResponsibilities);

public sealed record ComparativeAmount(decimal Current, decimal? Comparative = null);

public sealed record StatementOfFinancialPosition(
    ComparativeAmount FixedAssets,
    ComparativeAmount CurrentAssets,
    ComparativeAmount PrepaymentsAndAccruedIncome,
    ComparativeAmount CreditorsDueWithinOneYear,
    ComparativeAmount NetCurrentAssetsLiabilities,
    ComparativeAmount TotalAssetsLessCurrentLiabilities,
    ComparativeAmount CreditorsDueAfterOneYear,
    ComparativeAmount Provisions,
    ComparativeAmount AccrualsAndDeferredIncome,
    ComparativeAmount NetAssetsLiabilities,
    ComparativeAmount CapitalAndReserves);

public sealed record IncomeStatement(
    ComparativeAmount Turnover,
    ComparativeAmount OtherIncome,
    ComparativeAmount CostOfSales,
    ComparativeAmount AdministrativeExpenses,
    ComparativeAmount TaxOnProfit,
    ComparativeAmount ProfitLossForPeriod);

public sealed record DirectorAdvance(
    string DirectorName,
    decimal OpeningBalance,
    decimal Advances,
    decimal Repayments,
    decimal ClosingBalance,
    string Terms);

public sealed record CommitmentOrContingency(string Description, decimal? Amount);

public sealed record AccountsNotes(
    string PrincipalActivity,
    string AccountingPolicies,
    int AverageEmployees,
    IReadOnlyList<DirectorAdvance> DirectorAdvances,
    IReadOnlyList<CommitmentOrContingency> CommitmentsAndContingencies);

public sealed record StatutoryAccounts(
    CompanyIdentity Company,
    ReportingPeriod Period,
    ReportingPeriod? ComparativePeriod,
    AccountsProfile Profile,
    StatementOfFinancialPosition BalanceSheet,
    IncomeStatement IncomeStatement,
    AccountsNotes Notes,
    AccountsApproval Approval,
    string Currency = "GBP");
