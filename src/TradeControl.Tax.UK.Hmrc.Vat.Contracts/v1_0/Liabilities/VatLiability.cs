namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Liabilities;

public class VatLiability
{
    public required string Type { get; set; }
    public decimal OriginalAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public DateTime DueDate { get; set; }
    public string? ChargeRefNumber { get; set; }
}
