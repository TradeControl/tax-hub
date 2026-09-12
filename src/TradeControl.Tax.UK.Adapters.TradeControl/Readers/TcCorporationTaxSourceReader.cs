using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Company.Statutory;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Readers;

public sealed class TcCorporationTaxSourceReader : ICorporationTaxSource
{
    private const string AccountsSource = "UK-CO-ACCTS-2026";
    private const string CorporationTaxSource = "UK-CO-CT-2026";
    private readonly string _connectionString;
    private readonly TcStatutoryContextReader _contextReader;
    private readonly TcBusinessTaxReader _taxReader;

    public TcCorporationTaxSourceReader(ConnectionFactory connectionFactory, string connectionString)
    {
        _connectionString = connectionString;
        _contextReader = new(connectionFactory, connectionString);
        _taxReader = new(connectionFactory);
    }

    public async Task<CorporationTaxSource> ReadAsync(
        CorporationTaxProjectionRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = await _contextReader.ReadAsync(request.AsOfDate, cancellationToken);
        Require(context.Identity.BusinessTaxTypeCode == 0, "Corporation Tax requires a company Trade Control node.");

        var accountsPeriod = request.AccountsPeriod ?? context.BusinessTaxWindow;
        Require(accountsPeriod.End == request.AccountsPeriodEnd,
            "The selected accounts period does not end on the requested accounts-period end date.");
        var allocatedPeriods = Split(accountsPeriod);
        Require(request.ReviewedInput.Periods.Count == allocatedPeriods.Count,
            "Reviewed Corporation Tax inputs are required for every allocated Corporation Tax period.");

        var versions = context.Identity.Versions.Concat(context.Profiles.Select(value => value.Version)).ToList();
        var periods = new List<CorporationTaxPeriodSource>();
        foreach (var period in allocatedPeriods)
        {
            var reviewed = request.ReviewedInput.Periods.SingleOrDefault(value => value.Period == period)
                ?? throw new InvalidOperationException($"Reviewed inputs are missing for Corporation Tax period {period.Start:yyyy-MM-dd} to {period.End:yyyy-MM-dd}.");
            Validate(reviewed);

            var exclusiveEnd = period.End.AddDays(1).ToDateTime(TimeOnly.MinValue);
            var accounts = await _taxReader.ReadCumulativeAsync(_connectionString, AccountsSource,
                period.Start.ToDateTime(TimeOnly.MinValue), exclusiveEnd, cancellationToken);
            var tax = await _taxReader.ReadCumulativeAsync(_connectionString, CorporationTaxSource,
                period.Start.ToDateTime(TimeOnly.MinValue), exclusiveEnd, cancellationToken);
            var computation = await _taxReader.ReadCorporationTaxAsync(_connectionString,
                period.Start.ToDateTime(TimeOnly.MinValue), exclusiveEnd, cancellationToken);
            Require(accounts.ValidationStatus == TcTaxValidationStatus.Ready, "The accounts projection is not ready for Corporation Tax.");
            Require(tax.ValidationStatus == TcTaxValidationStatus.Ready, "The Corporation Tax projection is not ready.");
            Require(computation.IsUniformTaxRate && computation.BusinessTaxRate is not null,
                "The Corporation Tax period contains more than one business-tax rate and requires an apportioned computation.");
            var businessTaxRate = computation.BusinessTaxRate
                ?? throw new InvalidOperationException("The Corporation Tax rate is missing.");
            Require(computation.CalculatedTaxDue == computation.StatementTaxDue,
                "The calculated Corporation Tax does not reconcile to Cash.vwTaxBizStatement.");
            versions.AddRange(accounts.Versions);
            versions.AddRange(tax.Versions);
            versions.AddRange(computation.Versions);

            decimal Value(TcCumulativeProjection projection, string tag) => projection.Values.Single(value =>
                value.TagCode == tag && value.SupportStatus == TcTaxSupportStatus.Supported).StatutoryAmount!.Value;
            StatutorySourceValue<decimal> Source(decimal value, IReadOnlyList<SourceVersion> evidence) =>
                new(value, StatutoryValueState.Source, "TradeControl", evidence);
            StatutorySourceValue<decimal> Reviewed(decimal value) =>
                new(value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []);
            StatutorySourceValue<decimal> Derived(decimal value) =>
                new(value, StatutoryValueState.Derived, "CO4-Computation", accounts.Versions.Concat(tax.Versions).ToArray());

            var turnover = Value(accounts, "IncomeStatement.Turnover");
            var profitBeforeTax = turnover
                + Value(accounts, "IncomeStatement.OtherIncome")
                - Value(accounts, "IncomeStatement.CostOfSales")
                - Value(accounts, "IncomeStatement.AdministrativeExpenses");
            Require(profitBeforeTax == computation.NetProfit,
                "The accounts projection does not reconcile to Cash.vwTaxBizTotalsByPeriod net profit.");
            var depreciation = Value(tax, "AddBacks.AccountingDepreciation");
            var addBacks = new List<StatutorySourceValue<TaxAdjustmentDraft>>
            {
                new(new("Accounting depreciation", depreciation), StatutoryValueState.Source, "TradeControl", tax.Versions)
            };
            addBacks.AddRange(reviewed.OtherAddBacks.Select(value =>
                new StatutorySourceValue<TaxAdjustmentDraft>(value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", [])));
            var deductions = reviewed.Deductions.Select(value =>
                new StatutorySourceValue<TaxAdjustmentDraft>(value, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", [])).ToArray();
            var adjustedProfit = profitBeforeTax + addBacks.Sum(value => value.Value.Amount)
                - deductions.Sum(value => value.Value.Amount)
                - reviewed.CapitalAllowances.WritingDownAllowance
                - reviewed.CapitalAllowances.AnnualInvestmentAllowance
                - reviewed.CapitalAllowances.OtherAllowances;
            var broughtForwardLoss = computation.PreviousLossesCarriedForward;
            var currentLoss = computation.LossesCarriedForward - broughtForwardLoss + reviewed.LossesUsed;
            Require(currentLoss >= 0m && reviewed.LossesUsed <= broughtForwardLoss + currentLoss,
                "The reviewed loss claim cannot be reconciled to the statement-derived Corporation Tax loss movement.");
            var lossRelief = new LossReliefDraft(
                broughtForwardLoss, currentLoss, reviewed.LossesUsed, computation.LossesCarriedForward);
            var taxableProfits = Math.Max(0m, adjustedProfit + reviewed.ChargeableGains - reviewed.LossesUsed);
            var chargeable = Math.Max(0m,
                decimal.Round(taxableProfits * businessTaxRate, 5, MidpointRounding.AwayFromZero)
                + computation.BusinessTaxAdjustment);
            Require(chargeable == Math.Max(0m, computation.StatementTaxDue),
                "The reviewed Corporation Tax computation does not reconcile to Cash.vwTaxBizStatement.");
            var payable = Math.Max(0m, chargeable - reviewed.OtherReliefs);

            periods.Add(new(period,
                Source(turnover, accounts.Versions),
                Derived(profitBeforeTax),
                addBacks,
                deductions,
                new(reviewed.CapitalAllowances, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
                new(lossRelief, StatutoryValueState.Source, "Cash.vwTaxLossesCarriedForward", computation.Versions),
                Reviewed(reviewed.ChargeableGains),
                Derived(taxableProfits),
                Source(businessTaxRate, computation.Versions),
                Source(chargeable, computation.Versions),
                Reviewed(reviewed.OtherReliefs),
                Derived(payable),
                Source(Math.Abs(computation.StatementTaxPaid), computation.Versions),
                Source(computation.StatementBalance, computation.Versions),
                new(reviewed.LoansToParticipators, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", [])));
        }

        var input = request.ReviewedInput;
        return new(
            context.Identity.SubjectCode,
            context.Identity.SubjectName,
            input.CompanyNumber ?? context.Identity.CompanyNumber ?? string.Empty,
            input.Utr,
            accountsPeriod,
            periods,
            new(
                new(input.DeclarantName, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", []),
                new(input.DeclarationDate, StatutoryValueState.ReviewedFilingInput, "SubmissionOperator", [])),
            versions.Distinct().ToArray());
    }

    private static IReadOnlyList<ReportingWindow> Split(ReportingWindow period)
        => CorporationTaxPeriodAllocation.Split(new ReportingPeriod(period.Start, period.End))
            .CorporationTaxPeriods.Select(value => new ReportingWindow(value.Start, value.End)).ToArray();

    private static void Validate(CorporationTaxPeriodReviewedInput input)
    {
        Require(input.OtherAddBacks.All(value => value.Amount >= 0m), "Corporation Tax add-backs cannot be negative.");
        Require(input.Deductions.All(value => value.Amount >= 0m), "Corporation Tax deductions cannot be negative.");
        Require(input.CapitalAllowances.WritingDownAllowance >= 0m
            && input.CapitalAllowances.AnnualInvestmentAllowance >= 0m
            && input.CapitalAllowances.OtherAllowances >= 0m, "Capital allowances cannot be negative.");
        Require(input.LossesUsed >= 0m, "Corporation Tax losses used cannot be negative.");
        Require(input.OtherReliefs >= 0m, "Corporation Tax reliefs cannot be negative.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
