using System.Text.Json;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Submissions.MTDITSA.Obligations
{
    public class SaObligation
    {
        public string? PeriodKey { get; set; }
        public DateOnly Start { get; set; }
        public DateOnly End { get; set; }
        public DateOnly Due { get; set; }
        public string? Status { get; set; }
        public DateOnly? Received { get; set; }

        public SaObligation(JsonElement json)
        {
            PeriodKey = JsonExtract.GetString(json, "periodKey");
            Start = JsonExtract.GetDateOnly(json, "start");
            End = JsonExtract.GetDateOnly(json, "end");
            Due = JsonExtract.GetDateOnly(json, "due");
            Status = JsonExtract.GetString(json, "status");
            Received = JsonExtract.GetDateOnlyNullable(json, "received");
        }
    }
}
