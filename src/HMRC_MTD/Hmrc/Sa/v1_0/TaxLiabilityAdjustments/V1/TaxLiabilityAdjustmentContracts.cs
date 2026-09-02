using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.TaxLiabilityAdjustments.V1;

public static class TaxLiabilityAdjustmentEndpoints
{
    private const string Path = "/individuals/tax-liability/adjustments/{nino}/{taxYear}";
    private const string Accept = "application/vnd.hmrc.1.0+json";
    private static readonly EndpointParameter[] Parameters = [new("nino"), new("taxYear")];
    public static readonly HmrcEndpoint Get = new("Retrieve tax liability adjustments", "GET", Path, "1.0", Accept, "read:self-assessment", 200, Parameters, [], false, ResponseType: typeof(TaxLiabilityAdjustmentResource));
    public static readonly HmrcEndpoint Put = new("Create or amend tax liability adjustments", "PUT", Path, "1.0", Accept, "write:self-assessment", 204, Parameters, [], true, typeof(TaxLiabilityAdjustmentResource), ContentType: "application/json");
    public static readonly HmrcEndpoint Delete = new("Delete tax liability adjustments", "DELETE", Path, "1.0", Accept, "write:self-assessment", 204, Parameters, [], false);
    public static IReadOnlyList<HmrcEndpoint> All => [Get, Put, Delete];
}

public sealed class TaxLiabilityAdjustmentResource : HmrcResponse
{
    [JsonPropertyName("carryBackLossesDecrease")]
    public required CarryBackLossesDecrease CarryBackLossesDecrease { get; set; }
}

public sealed class CarryBackLossesDecrease
{
    [JsonPropertyName("incomeTax")] public decimal? IncomeTax { get; set; }
    [JsonPropertyName("class4")] public decimal? Class4 { get; set; }
    [JsonPropertyName("capitalGainsTax")] public decimal? CapitalGainsTax { get; set; }
}
