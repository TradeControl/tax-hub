namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TcVatStatement
{
    public int YearNumber { get; init; }
    public string Description { get; init; } = string.Empty;
    public string Period { get; init; } = string.Empty;
    public DateTime StartOn { get; init; }
    public DateTime VatEndOn { get; init; }
    public decimal VatDueSales { get; init; }
    public decimal VatDueAcquisitions { get; init; }
    public decimal TotalVatDue { get; init; }
    public decimal VatReclaimedCurrPeriod { get; init; }
    public decimal NetVatDue { get; init; }
    public decimal TotalValueSalesExVat { get; init; }
    public decimal TotalValuePurchasesExVat { get; init; }
    public decimal TotalValueGoodsSuppliedExVat { get; init; }
    public decimal TotalValueGoodsReceivedExVat { get; init; }
}
