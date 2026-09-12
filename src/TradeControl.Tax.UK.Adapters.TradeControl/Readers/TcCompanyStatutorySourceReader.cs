using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Readers;

public sealed class TcCompanyStatutorySourceReader : ICompanyStatutorySource
{
    private const string AccountsSource = "UK-CO-ACCTS-2026";
    private readonly string _connectionString;
    private readonly TcStatutoryContextReader _contextReader;
    private readonly TcBusinessTaxReader _taxReader;

    public TcCompanyStatutorySourceReader(ConnectionFactory connectionFactory, string connectionString)
    {
        _connectionString = connectionString;
        _contextReader = new(connectionFactory, connectionString);
        _taxReader = new(connectionFactory);
    }

    public async Task<CompanyStatutorySource> ReadAsync(
        CompanyProjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await _contextReader.ReadAsync(request.AsOfDate, cancellationToken);
        if (context.Identity.BusinessTaxTypeCode != 0)
            throw new InvalidOperationException("Company accounts require a company Trade Control node.");

        var defaults = CompanyAccountsDraftDefaults.Create(context);
        var current = request.Period ?? defaults.Period.Value;
        var comparative = request.IsFirstAccountsPeriod
            ? null
            : request.ComparativePeriod ?? defaults.ComparativePeriod.Value;

        var currentIncome = await _taxReader.ReadCumulativeAsync(
            _connectionString, AccountsSource,
            current.Start.ToDateTime(TimeOnly.MinValue), current.End.AddDays(1).ToDateTime(TimeOnly.MinValue), cancellationToken);
        var comparativeIncome = comparative is null ? null : await _taxReader.ReadCumulativeAsync(
            _connectionString, AccountsSource,
            comparative.Start.ToDateTime(TimeOnly.MinValue), comparative.End.AddDays(1).ToDateTime(TimeOnly.MinValue), cancellationToken);
        var currentBalance = await _taxReader.ReadBalanceSheetAsync(
            _connectionString, AccountsSource, current.End.ToDateTime(TimeOnly.MinValue), cancellationToken);
        var comparativeBalance = comparative is null ? null : await _taxReader.ReadBalanceSheetAsync(
            _connectionString, AccountsSource, comparative.End.ToDateTime(TimeOnly.MinValue), cancellationToken);

        RequireReady(currentIncome.ValidationStatus, "current income statement");
        RequireReady(currentBalance.ValidationStatus, "current balance sheet");
        if (comparativeIncome is not null) RequireReady(comparativeIncome.ValidationStatus, "comparative income statement");
        if (comparativeBalance is not null) RequireReady(comparativeBalance.ValidationStatus, "comparative balance sheet");

        var versions = context.Identity.Versions
            .Concat(context.Profiles.Select(value => value.Version))
            .Concat(context.Settings.Select(value => value.Version))
            .Concat(currentIncome.Versions)
            .Concat(comparativeIncome?.Versions ?? [])
            .Concat(currentBalance.Versions)
            .Concat(comparativeBalance?.Versions ?? [])
            .Distinct()
            .ToArray();
        var reviewed = request.ReviewedInput;
        var identity = context.Identity with
        {
            CompanyNumber = reviewed.CompanyNumber ?? context.Identity.CompanyNumber
        };

        decimal Income(TcCumulativeProjection projection, string tag) => projection.Values.Single(value =>
            value.TagCode == tag && value.SupportStatus == TcTaxSupportStatus.Supported).StatutoryAmount!.Value;
        decimal Balance(TcBalanceSheetProjection projection, string tag) => projection.Values.Single(value =>
            value.TagCode == tag && value.SupportStatus == TcTaxSupportStatus.Supported).StatutoryAmount!.Value;
        StatutorySourceValue<decimal> S(decimal value, IReadOnlyList<SourceVersion> evidence) =>
            new(value, StatutoryValueState.Source, "TradeControl", evidence);
        StatutorySourceValue<decimal> R(decimal value) =>
            new(value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []);
        StatutorySourceValue<decimal> D(decimal value) =>
            new(value, StatutoryValueState.Derived, "CO3-Reconciliation", versions);
        ComparativeStatutoryAmount Pair(decimal currentValue, decimal? comparativeValue,
            IReadOnlyList<SourceVersion> currentEvidence, IReadOnlyList<SourceVersion>? comparativeEvidence = null) =>
            new(S(currentValue, currentEvidence), comparativeValue is null ? null : S(comparativeValue.Value, comparativeEvidence ?? []));
        ComparativeStatutoryAmount ReviewedPair(decimal currentValue, decimal? comparativeValue) =>
            new(R(currentValue), comparativeValue is null ? null : R(comparativeValue.Value));
        ComparativeStatutoryAmount DerivedPair(decimal currentValue, decimal? comparativeValue) =>
            new(D(currentValue), comparativeValue is null ? null : D(comparativeValue.Value));

        var turnover = Pair(Income(currentIncome, "IncomeStatement.Turnover"),
            comparativeIncome is null ? null : Income(comparativeIncome, "IncomeStatement.Turnover"),
            currentIncome.Versions, comparativeIncome?.Versions);
        var otherIncome = Pair(Income(currentIncome, "IncomeStatement.OtherIncome"),
            comparativeIncome is null ? null : Income(comparativeIncome, "IncomeStatement.OtherIncome"),
            currentIncome.Versions, comparativeIncome?.Versions);
        var costOfSales = Pair(Income(currentIncome, "IncomeStatement.CostOfSales"),
            comparativeIncome is null ? null : Income(comparativeIncome, "IncomeStatement.CostOfSales"),
            currentIncome.Versions, comparativeIncome?.Versions);
        var admin = Pair(Income(currentIncome, "IncomeStatement.AdministrativeExpenses"),
            comparativeIncome is null ? null : Income(comparativeIncome, "IncomeStatement.AdministrativeExpenses"),
            currentIncome.Versions, comparativeIncome?.Versions);
        var profit = turnover.Current.Value + otherIncome.Current.Value - costOfSales.Current.Value
            - admin.Current.Value - reviewed.TaxOnProfit;
        decimal? comparativeProfit = comparative is null ? null
            : turnover.Comparative!.Value + otherIncome.Comparative!.Value - costOfSales.Comparative!.Value
              - admin.Comparative!.Value - (reviewed.ComparativeTaxOnProfit ?? 0m);

        decimal CB(string tag) => Balance(currentBalance, tag);
        decimal? PB(string tag) => comparativeBalance is null ? null : Balance(comparativeBalance, tag);
        var fixedAssets = Pair(CB("BalanceSheet.FixedAssets"), PB("BalanceSheet.FixedAssets"), currentBalance.Versions, comparativeBalance?.Versions);
        var currentAssets = Pair(CB("BalanceSheet.CurrentAssets"), PB("BalanceSheet.CurrentAssets"), currentBalance.Versions, comparativeBalance?.Versions);
        var creditorsWithin = Pair(CB("BalanceSheet.CreditorsDueWithinOneYear"), PB("BalanceSheet.CreditorsDueWithinOneYear"), currentBalance.Versions, comparativeBalance?.Versions);
        var creditorsAfter = Pair(CB("BalanceSheet.CreditorsDueAfterOneYear"), PB("BalanceSheet.CreditorsDueAfterOneYear"), currentBalance.Versions, comparativeBalance?.Versions);
        var prepayments = ReviewedPair(reviewed.PrepaymentsAndAccruedIncome,
            comparative is null ? null : reviewed.ComparativePrepaymentsAndAccruedIncome ?? 0m);
        var provisions = ReviewedPair(reviewed.Provisions,
            comparative is null ? null : reviewed.ComparativeProvisions ?? 0m);
        var accruals = ReviewedPair(reviewed.AccrualsAndDeferredIncome,
            comparative is null ? null : reviewed.ComparativeAccrualsAndDeferredIncome ?? 0m);
        var netCurrent = DerivedPair(currentAssets.Current.Value + prepayments.Current.Value - creditorsWithin.Current.Value,
            comparative is null ? null : currentAssets.Comparative!.Value + prepayments.Comparative!.Value - creditorsWithin.Comparative!.Value);
        var totalAssets = DerivedPair(fixedAssets.Current.Value + netCurrent.Current.Value,
            comparative is null ? null : fixedAssets.Comparative!.Value + netCurrent.Comparative!.Value);
        var netAssets = DerivedPair(totalAssets.Current.Value - creditorsAfter.Current.Value - provisions.Current.Value - accruals.Current.Value,
            comparative is null ? null : totalAssets.Comparative!.Value - creditorsAfter.Comparative!.Value - provisions.Comparative!.Value - accruals.Comparative!.Value);

        var profile = context.Profiles.Single(value => value.ReportingTypeCode == "STATUTORY-ACCOUNTS" && value.IsReviewed);
        string Setting(string code) => context.Settings.Single(value =>
            value.ProfileCode == profile.ProfileCode && value.SettingCode == code && value.IsReviewed).DisplayValue;
        if (Setting("ACCOUNTING-STANDARD") != "FRS-105" || Setting("ACCOUNTS-TYPE") != "MICRO-ENTITY")
            throw new InvalidOperationException("Only the reviewed FRS 105 micro-entity profile is supported.");

        var notes = new CompanyNotesSource(
            new(reviewed.PrincipalActivity ?? defaults.PrincipalActivity.Value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
            new(reviewed.AccountingPolicies ?? defaults.AccountingPolicies.Value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
            new(reviewed.AverageEmployees ?? defaults.AverageEmployees.Value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
            reviewed.DirectorAdvances.Select(value => new DirectorAdvanceSource(value.DirectorName,
                R(value.OpeningBalance), R(value.Advances), R(value.Repayments), R(value.ClosingBalance),
                new(value.Terms, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []))).ToArray(),
            reviewed.CommitmentsAndContingencies.Select(value => new CommitmentSource(
                new(value.Description, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
                new(value.Amount, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []))).ToArray());

        return new(identity,
            new(current, comparative, new(request.IsFirstAccountsPeriod, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", [])),
            new(CompanyReportingFramework.Frs105, CompanyAccountsType.MicroEntity, CompanyAuditStatus.UnauditedExempt,
                new(reviewed.MembersHaveNotRequiredAudit, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
                new(reviewed.DirectorsAcknowledgeResponsibilities, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", [])),
            new(fixedAssets, currentAssets, prepayments, creditorsWithin, netCurrent, totalAssets,
                creditorsAfter, provisions, accruals, netAssets, netAssets),
            new(turnover, otherIncome, costOfSales, admin,
                ReviewedPair(reviewed.TaxOnProfit, comparative is null ? null : reviewed.ComparativeTaxOnProfit ?? 0m),
                DerivedPair(profit, comparativeProfit)),
            notes,
            new(new(reviewed.ApprovedOn, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
                new(reviewed.SigningDirectorCode, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
                new(reviewed.SigningDirectorName, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", [])),
            versions);
    }

    private static void RequireReady(TcTaxValidationStatus status, string source)
    {
        if (status != TcTaxValidationStatus.Ready)
            throw new InvalidOperationException($"The {source} projection is not ready.");
    }
}
