namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;

public class VatFinancialDetail
{
    public decimal Amount { get; set; }
    public DateTime PostingDate { get; set; }
    public string? ChargeRefNumber { get; set; }
    public string? Description { get; set; }
}
