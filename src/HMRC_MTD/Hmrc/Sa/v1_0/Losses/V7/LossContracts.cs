using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.Losses.V7;

public static class LossV7Endpoints
{
    private const string Path = "/individuals/losses/{nino}/businesses/{businessId}/loss-claims/{taxYear}";
    private const string Accept = "application/vnd.hmrc.7.0+json";
    private static readonly EndpointParameter[] Parameters = [new("nino"), new("businessId"), new("taxYear")];
    public static readonly HmrcEndpoint Get = new("Retrieve losses and claims", "GET", Path, "7.0", Accept, "read:self-assessment", 200, Parameters, [], false, ResponseType: typeof(LossClaimsResource));
    public static readonly HmrcEndpoint Put = new("Create or amend losses and claims", "PUT", Path, "7.0", Accept, "write:self-assessment", 204, Parameters, [], true, typeof(LossClaimsResource), ContentType: "application/json");
    public static readonly HmrcEndpoint Delete = new("Delete losses and claims", "DELETE", Path, "7.0", Accept, "write:self-assessment", 204, Parameters, [], false);
    public static IReadOnlyList<HmrcEndpoint> All => [Get, Put, Delete];
}

public sealed class LossClaimsResource : HmrcResponse
{
    [JsonPropertyName("claims")] public LossClaims? Claims { get; set; }
    [JsonPropertyName("losses")] public Losses? Losses { get; set; }
}

public sealed class LossClaims
{
    [JsonPropertyName("carryBack")] public CarryBackClaims? CarryBack { get; set; }
    [JsonPropertyName("carrySideways")] public CarrySidewaysClaims? CarrySideways { get; set; }
    [JsonPropertyName("preferenceOrder")] public PreferenceOrder? PreferenceOrder { get; set; }
    [JsonPropertyName("carryForward")] public CarryForwardClaims? CarryForward { get; set; }
}

public sealed class CarryBackClaims
{
    [JsonPropertyName("previousYearGeneralIncome")] public decimal? PreviousYearGeneralIncome { get; set; }
    [JsonPropertyName("earlyYearLosses")] public decimal? EarlyYearLosses { get; set; }
    [JsonPropertyName("terminalLosses")] public decimal? TerminalLosses { get; set; }
}

public sealed class CarrySidewaysClaims
{
    [JsonPropertyName("currentYearGeneralIncome")] public decimal? CurrentYearGeneralIncome { get; set; }
}

public sealed class PreferenceOrder
{
    [JsonPropertyName("applyFirst")] public required string ApplyFirst { get; set; }
}

public sealed class CarryForwardClaims
{
    [JsonPropertyName("currentYearLosses")] public decimal? CurrentYearLosses { get; set; }
    [JsonPropertyName("previousYearsLosses")] public decimal? PreviousYearsLosses { get; set; }
}

public sealed class Losses
{
    [JsonPropertyName("broughtForwardLosses")] public decimal? BroughtForwardLosses { get; set; }
}
