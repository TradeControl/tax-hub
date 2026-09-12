namespace TradeControl.Tax.UK.Company.Statutory;

public sealed record TaxAdjustment(string Description, decimal Amount);

public sealed record CapitalAllowanceSchedule(
    decimal WritingDownAllowance,
    decimal AnnualInvestmentAllowance,
    decimal OtherAllowances)
{
    public decimal Total => WritingDownAllowance + AnnualInvestmentAllowance + OtherAllowances;
}

public sealed record LossReliefSchedule(
    decimal BroughtForward,
    decimal CurrentPeriod,
    decimal Used,
    decimal CarriedForward);

public sealed record CorporationTaxComputation(
    ReportingPeriod CorporationTaxPeriod,
    decimal AccountsProfitLossBeforeTax,
    IReadOnlyList<TaxAdjustment> AddBacks,
    IReadOnlyList<TaxAdjustment> Deductions,
    CapitalAllowanceSchedule CapitalAllowances,
    LossReliefSchedule Losses,
    decimal ChargeableGains,
    decimal TaxableTotalProfits,
    decimal MainRate,
    decimal CorporationTaxChargeable,
    decimal Reliefs,
    decimal TaxPayable)
{
    public decimal AdjustedTradingProfit => AccountsProfitLossBeforeTax
        + AddBacks.Sum(x => x.Amount)
        - Deductions.Sum(x => x.Amount)
        - CapitalAllowances.Total;
}

public sealed record CorporationTaxPeriodAllocation(
    ReportingPeriod AccountsPeriod,
    IReadOnlyList<ReportingPeriod> CorporationTaxPeriods)
{
    public static CorporationTaxPeriodAllocation Split(ReportingPeriod accountsPeriod)
    {
        if (accountsPeriod.InclusiveDays <= 366)
            return new(accountsPeriod, [accountsPeriod]);

        var firstEnd = accountsPeriod.Start.AddYears(1).AddDays(-1);
        return new(accountsPeriod,
        [
            new ReportingPeriod(accountsPeriod.Start, firstEnd),
            new ReportingPeriod(firstEnd.AddDays(1), accountsPeriod.End)
        ]);
    }
}
