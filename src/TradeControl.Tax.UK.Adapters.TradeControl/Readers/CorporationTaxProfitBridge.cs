namespace TradeControl.Tax.UK.Adapters.TradeControl.Readers;

/// <summary>
/// Keeps accounting profit and the depreciation tax adjustment as distinct statutory facts.
/// Administrative expenses already contain accounting depreciation, so the expense must not be
/// removed before it is added back once in the Corporation Tax computation.
/// </summary>
public sealed record CorporationTaxProfitBridge(
    decimal AccountingProfitBeforeTax,
    decimal DepreciationAddBack,
    decimal AdjustedProfitBeforeOtherTaxAdjustments)
{
    public static CorporationTaxProfitBridge Create(
        decimal turnover,
        decimal otherIncome,
        decimal costOfSales,
        decimal administrativeExpenses,
        decimal accountingDepreciation)
    {
        var accountingProfit = turnover + otherIncome - costOfSales - administrativeExpenses;
        var depreciationAddBack = Math.Abs(accountingDepreciation);
        return new(accountingProfit, depreciationAddBack, accountingProfit + depreciationAddBack);
    }
}
