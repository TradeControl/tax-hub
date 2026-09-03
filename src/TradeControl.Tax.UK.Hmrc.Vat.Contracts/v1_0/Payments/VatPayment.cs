namespace TradeControl.Tax.UK.Hmrc.Vat.v1_0.Payments;

public class VatPayment
{
    public decimal Amount { get; set; }
    public DateTime Received { get; set; }
    public string? ChargeRefNumber { get; set; }
    public string? Method { get; set; }
}
