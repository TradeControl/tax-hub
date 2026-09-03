using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Liabilities;

public class SaBalanceDetails
{
    public decimal TotalBalance { get; set; }
    public decimal AvailableCredit { get; set; }
    public decimal PendingRefund { get; set; }
    public decimal OverdueAmount { get; set; }

    public SaBalanceDetails(JsonElement json)
    {
        TotalBalance = JsonExtract.GetDecimal(json, "totalBalance");
        AvailableCredit = JsonExtract.GetDecimal(json, "availableCredit");
        PendingRefund = JsonExtract.GetDecimal(json, "pendingRefund");
        OverdueAmount = JsonExtract.GetDecimal(json, "overdueAmount");
    }
}
