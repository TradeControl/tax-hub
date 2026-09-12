using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026;

namespace TradeControl.Tax.UK.Application.Preparation;

public sealed record PopulatedCorporationTaxReturn(
    CorporationTaxComputation Computation,
    Ct600Return Return);

public sealed class CorporationTaxPopulator
{
    public IReadOnlyList<PopulatedCorporationTaxReturn> Populate(CorporationTaxSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Require(!string.IsNullOrWhiteSpace(source.CompanyName), "The company name is missing.");
        Require(!string.IsNullOrWhiteSpace(source.CompanyNumber), "The company number is missing.");
        Require(!string.IsNullOrWhiteSpace(source.Utr), "The Corporation Tax UTR is missing.");
        Require(source.Declaration.DeclarantName.State == StatutoryValueState.ReviewedFilingInput
            && !string.IsNullOrWhiteSpace(source.Declaration.DeclarantName.Value), "The CT600 declarant must be reviewed.");
        Require(source.Declaration.DeclarationDate.State == StatutoryValueState.ReviewedFilingInput,
            "The CT600 declaration date must be reviewed.");

        return source.CorporationTaxPeriods.Select(period =>
        {
            var computation = new CorporationTaxComputation(
                Period(period.Period),
                period.AccountsProfitLossBeforeTax.Value,
                period.AddBacks.Select(value => new TaxAdjustment(value.Value.Description, value.Value.Amount)).ToArray(),
                period.Deductions.Select(value => new TaxAdjustment(value.Value.Description, value.Value.Amount)).ToArray(),
                new(period.CapitalAllowances.Value.WritingDownAllowance,
                    period.CapitalAllowances.Value.AnnualInvestmentAllowance,
                    period.CapitalAllowances.Value.OtherAllowances),
                new(period.LossRelief.Value.BroughtForward,
                    period.LossRelief.Value.CurrentPeriod,
                    period.LossRelief.Value.Used,
                    period.LossRelief.Value.CarriedForward),
                period.ChargeableGains.Value,
                period.TaxableTotalProfits.Value,
                period.MainRate.Value,
                period.CorporationTaxChargeable.Value,
                period.OtherReliefs.Value,
                period.TaxPayable.Value);
            var loans = period.LoansToParticipators.Value;
            var ct600 = new Ct600Return(
                source.CompanyName,
                source.CompanyNumber,
                source.Utr,
                Period(period.Period),
                period.Turnover.Value,
                period.AccountsProfitLossBeforeTax.Value,
                period.TaxableTotalProfits.Value,
                period.CorporationTaxChargeable.Value,
                period.TaxPayable.Value,
                true,
                true,
                loans is null ? null : new(loans.LoansOutstandingAtPeriodEnd, loans.TaxChargeable, loans.TaxPaid),
                source.Declaration.DeclarantName.Value,
                source.Declaration.DeclarationDate.Value);
            return new PopulatedCorporationTaxReturn(computation, ct600);
        }).ToArray();
    }

    private static ReportingPeriod Period(ReportingWindow period) => new(period.Start, period.End);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
