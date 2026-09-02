using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessIncomeSummary.V3.Wire;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.BusinessIncomeSummary.V3;

public static class BusinessIncomeSummaryEndpoints
{
    public static readonly HmrcEndpoint Retrieve = new("Retrieve business income source summary", "GET", "/individuals/self-assessment/income-summary/{nino}/{typeOfBusiness}/{taxYear}/{businessId}", "3.0", "application/vnd.hmrc.3.0+json", "read:self-assessment", 200, [new("nino"), new("typeOfBusiness"), new("taxYear"), new("businessId")], [], false, ResponseType: typeof(BissWireResponse));
    public static IReadOnlyList<HmrcEndpoint> All => [Retrieve];
}
