namespace TradeControl.Tax.UK.Company.Statutory;

public sealed record CorporationTaxRateBand(decimal Profit, decimal Rate)
{
    public decimal Tax => CorporationTaxFinancialYearAllocation.PoundPence(Profit * Rate);
}

public sealed record CorporationTaxFinancialYearCharge(
    int FinancialYear,
    IReadOnlyList<CorporationTaxRateBand> Bands)
{
    public decimal Profit => Bands.Sum(band => band.Profit);
    public decimal Tax => Bands.Sum(band => band.Tax);
}

public sealed record CorporationTaxFinancialYearAllocation(
    IReadOnlyList<CorporationTaxFinancialYearCharge> Years)
{
    public decimal CorporationTax => Years.Sum(year => year.Tax);

    public static CorporationTaxFinancialYearAllocation Create(CorporationTaxComputation computation)
    {
        var liability = computation.Liability;
        var period = computation.CorporationTaxPeriod;
        var firstYear = FinancialYear(period.Start);
        var firstYearEnd = new DateOnly(firstYear + 1, 3, 31);
        var firstDays = Math.Min(period.InclusiveDays,
            (period.End < firstYearEnd ? period.End : firstYearEnd).DayNumber - period.Start.DayNumber + 1);
        var totalProfit = WholePounds(computation.TaxableTotalProfits);
        var firstProfit = firstDays == period.InclusiveDays
            ? totalProfit
            : WholePounds(decimal.Round(totalProfit * firstDays / period.InclusiveDays,
                0, MidpointRounding.AwayFromZero));
        var secondProfit = totalProfit - firstProfit;
        // CT600 box 430 carries tax at the applicable statutory rate. Marginal
        // relief is then declared separately in box 435 and deducted in box 440;
        // it must not be represented as a synthetic split between the small and
        // main rates.
        var smallProfit = liability.CalculationCode == "SMALL-PROFITS" ? totalProfit : 0m;
        smallProfit = Math.Min(totalProfit, smallProfit);
        var firstSmallProfit = firstDays == period.InclusiveDays
            ? smallProfit
            : WholePounds(decimal.Round(smallProfit * firstDays / period.InclusiveDays,
                0, MidpointRounding.AwayFromZero));
        var secondSmallProfit = smallProfit - firstSmallProfit;

        var years = new List<CorporationTaxFinancialYearCharge>
        {
            new(firstYear, Bands(firstProfit, firstSmallProfit, liability))
        };
        if (secondProfit > 0m)
            years.Add(new(firstYear + 1, Bands(secondProfit, secondSmallProfit, liability)));
        return new(years);
    }

    internal static decimal PoundPence(decimal value) =>
        decimal.Parse((decimal.Truncate(value * 100m) / 100m).ToString("0.00",
            System.Globalization.CultureInfo.InvariantCulture),
            System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyList<CorporationTaxRateBand> Bands(
        decimal profit,
        decimal smallProfit,
        CorporationTaxLiabilityCalculation liability)
    {
        var bands = new List<CorporationTaxRateBand>();
        if (smallProfit > 0m)
            bands.Add(new(smallProfit, liability.SmallProfitsRate));
        if (profit - smallProfit > 0m)
            bands.Add(new(profit - smallProfit, liability.MainRate));
        if (bands.Count == 0)
            bands.Add(new(0m, liability.CalculationCode == "SMALL-PROFITS"
                ? liability.SmallProfitsRate
                : liability.MainRate));
        return bands;
    }

    private static decimal WholePounds(decimal value) => decimal.Truncate(value);
    private static int FinancialYear(DateOnly date) => date.Month >= 4 ? date.Year : date.Year - 1;
}
