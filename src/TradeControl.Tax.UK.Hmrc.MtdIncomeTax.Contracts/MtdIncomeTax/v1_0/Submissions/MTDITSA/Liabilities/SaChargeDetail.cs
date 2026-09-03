using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Liabilities;

public class SaChargeDetail
{
    public string? ChargeType { get; set; }
    public string? ChargeReference { get; set; }
    public decimal Amount { get; set; }
    public DateOnly DueDate { get; set; }
    public decimal OutstandingAmount { get; set; }
    public decimal? Interest { get; set; }

    public SaChargeDetail(JsonElement json)
    {
        ChargeType = JsonExtract.GetString(json, "chargeType");
        ChargeReference = JsonExtract.GetString(json, "chargeReference");
        Amount = JsonExtract.GetDecimal(json, "amount");
        DueDate = JsonExtract.GetDateOnly(json, "dueDate");
        OutstandingAmount = JsonExtract.GetDecimal(json, "outstandingAmount");
        Interest = JsonExtract.GetDecimalNullable(json, "interest");
    }
}
