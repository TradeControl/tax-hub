namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Penalties;

public class VatPenalty
{
    public required string PenaltyType { get; set; }
    public decimal Amount { get; set; }
    public DateTime IssueDate { get; set; }
    public string? ChargeRefNumber { get; set; }
}
