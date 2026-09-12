namespace TradeControl.Tax.UK.Application.DataProvision;

public enum StatutoryValueState
{
    Source,
    ReviewedFilingInput,
    Derived,
    ExplicitZero,
    NotApplicable
}

public sealed record StatutorySourceValue<T>(
    T Value,
    StatutoryValueState State,
    string SourceCode,
    IReadOnlyList<SourceVersion> Versions);

public sealed record CompanyProjectionRequest(
    DateOnly AsOfDate,
    ReportingWindow? Period,
    ReportingWindow? ComparativePeriod,
    bool IsFirstAccountsPeriod,
    CompanyAccountsReviewedInput ReviewedInput);

public sealed record CompanyAccountsReviewedInput(
    string? CompanyNumber,
    bool MembersHaveNotRequiredAudit,
    bool DirectorsAcknowledgeResponsibilities,
    decimal TaxOnProfit,
    decimal? ComparativeTaxOnProfit,
    decimal PrepaymentsAndAccruedIncome,
    decimal? ComparativePrepaymentsAndAccruedIncome,
    decimal Provisions,
    decimal? ComparativeProvisions,
    decimal AccrualsAndDeferredIncome,
    decimal? ComparativeAccrualsAndDeferredIncome,
    string? PrincipalActivity,
    string? AccountingPolicies,
    int? AverageEmployees,
    IReadOnlyList<DirectorAdvanceDraft> DirectorAdvances,
    IReadOnlyList<CommitmentDraft> CommitmentsAndContingencies,
    DateOnly ApprovedOn,
    string SigningDirectorCode,
    string SigningDirectorName);

public sealed record CompanyPeriodSource(
    ReportingWindow Current,
    ReportingWindow? Comparative,
    StatutorySourceValue<bool> IsFirstAccountsPeriod);

public enum CompanyReportingFramework
{
    Frs105
}

public enum CompanyAccountsType
{
    MicroEntity
}

public enum CompanyAuditStatus
{
    UnauditedExempt,
    Audited
}

public sealed record CompanyAccountsProfileSource(
    CompanyReportingFramework Framework,
    CompanyAccountsType AccountsType,
    CompanyAuditStatus AuditStatus,
    StatutorySourceValue<bool> MembersHaveNotRequiredAudit,
    StatutorySourceValue<bool> DirectorsAcknowledgeResponsibilities);

public sealed record ComparativeStatutoryAmount(
    StatutorySourceValue<decimal> Current,
    StatutorySourceValue<decimal>? Comparative);

public sealed record CompanyBalanceSheetSource(
    ComparativeStatutoryAmount FixedAssets,
    ComparativeStatutoryAmount CurrentAssets,
    ComparativeStatutoryAmount PrepaymentsAndAccruedIncome,
    ComparativeStatutoryAmount CreditorsDueWithinOneYear,
    ComparativeStatutoryAmount NetCurrentAssetsLiabilities,
    ComparativeStatutoryAmount TotalAssetsLessCurrentLiabilities,
    ComparativeStatutoryAmount CreditorsDueAfterOneYear,
    ComparativeStatutoryAmount Provisions,
    ComparativeStatutoryAmount AccrualsAndDeferredIncome,
    ComparativeStatutoryAmount NetAssetsLiabilities,
    ComparativeStatutoryAmount CapitalAndReserves);

public sealed record CompanyIncomeStatementSource(
    ComparativeStatutoryAmount Turnover,
    ComparativeStatutoryAmount OtherIncome,
    ComparativeStatutoryAmount CostOfSales,
    ComparativeStatutoryAmount AdministrativeExpenses,
    ComparativeStatutoryAmount TaxOnProfit,
    ComparativeStatutoryAmount ProfitLossForPeriod);

public sealed record DirectorAdvanceSource(
    string DirectorName,
    StatutorySourceValue<decimal> OpeningBalance,
    StatutorySourceValue<decimal> Advances,
    StatutorySourceValue<decimal> Repayments,
    StatutorySourceValue<decimal> ClosingBalance,
    StatutorySourceValue<string> Terms);

public sealed record CommitmentSource(
    StatutorySourceValue<string> Description,
    StatutorySourceValue<decimal?> Amount);

public sealed record CompanyNotesSource(
    StatutorySourceValue<string> PrincipalActivity,
    StatutorySourceValue<string> AccountingPolicies,
    StatutorySourceValue<int> AverageEmployees,
    IReadOnlyList<DirectorAdvanceSource> DirectorAdvances,
    IReadOnlyList<CommitmentSource> CommitmentsAndContingencies);

public sealed record CompanyApprovalSource(
    StatutorySourceValue<DateOnly> ApprovedOn,
    StatutorySourceValue<string> SigningDirectorCode,
    StatutorySourceValue<string> SigningDirectorName);

public sealed record CompanyStatutorySource(
    StatutoryIdentityEvidence Identity,
    CompanyPeriodSource Periods,
    CompanyAccountsProfileSource Profile,
    CompanyBalanceSheetSource BalanceSheet,
    CompanyIncomeStatementSource IncomeStatement,
    CompanyNotesSource Notes,
    CompanyApprovalSource Approval,
    IReadOnlyList<SourceVersion> Versions);

public interface ICompanyStatutorySource
{
    Task<CompanyStatutorySource> ReadAsync(
        CompanyProjectionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CorporationTaxProjectionRequest(
    DateOnly AccountsPeriodEnd,
    DateOnly AsOfDate);

public sealed record CorporationTaxPeriodSource(
    ReportingWindow Period,
    StatutorySourceValue<decimal> AccountsProfitLossBeforeTax,
    IReadOnlyList<StatutorySourceValue<TaxAdjustmentDraft>> AddBacks,
    IReadOnlyList<StatutorySourceValue<TaxAdjustmentDraft>> Deductions,
    StatutorySourceValue<CapitalAllowanceDraft> CapitalAllowances,
    StatutorySourceValue<LossReliefDraft> LossRelief,
    StatutorySourceValue<decimal> ChargeableGains,
    StatutorySourceValue<decimal> TaxableTotalProfits,
    StatutorySourceValue<decimal> MainRate,
    StatutorySourceValue<decimal> CorporationTaxChargeable,
    StatutorySourceValue<decimal> OtherReliefs,
    StatutorySourceValue<decimal> TaxPayable,
    StatutorySourceValue<decimal> TaxPaid);

public sealed record CorporationTaxSource(
    string SubjectCode,
    string CompanyNumber,
    string UtrDisplayValue,
    ReportingWindow AccountsPeriod,
    IReadOnlyList<CorporationTaxPeriodSource> CorporationTaxPeriods,
    IReadOnlyList<SourceVersion> Versions);

public interface ICorporationTaxSource
{
    Task<CorporationTaxSource> ReadAsync(
        CorporationTaxProjectionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CompanySourceScenario(
    bool IsPrivateCompany,
    bool IsMicroEntity,
    bool RequiresGroupAccounts = false,
    bool HasMultipleTrades = false,
    bool HasSpecialistActivities = false,
    bool RequiresUnsupportedSupplementaryReturn = false);

public sealed class UnsupportedCompanySourceScenarioException : InvalidOperationException
{
    public UnsupportedCompanySourceScenarioException(string scenario)
        : base($"The company source scenario '{scenario}' is not supported.") => Scenario = scenario;

    public string Scenario { get; }
}

public static class CompanySourceSupport
{
    public static void RequireOrdinaryPrivateMicroCompany(CompanySourceScenario scenario)
    {
        Require(scenario.IsPrivateCompany, "non-private-company");
        Require(scenario.IsMicroEntity, "non-micro-entity");
        Require(!scenario.RequiresGroupAccounts, "group-accounts");
        Require(!scenario.HasMultipleTrades, "multiple-trades");
        Require(!scenario.HasSpecialistActivities, "specialist-activities");
        Require(!scenario.RequiresUnsupportedSupplementaryReturn, "unsupported-supplementary-return");
    }

    private static void Require(bool condition, string scenario)
    {
        if (!condition)
            throw new UnsupportedCompanySourceScenarioException(scenario);
    }
}
