namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Obligations;

public class VatObligation
{
    public required string ObligationId { get; set; }
    public required string PeriodKey { get; set; }
    public DateTime Start { get; set; }
    public DateTime End { get; set; }
    public DateTime Due { get; set; }
    public DateTime? Received { get; set; }
    public required string Status { get; set; }
}
