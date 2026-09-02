using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.Losses.V6;

public static class LossV6Endpoints
{
    private const string Accept = "application/vnd.hmrc.6.0+json";
    private const string Root = "/individuals/losses/{nino}";
    public static readonly HmrcEndpoint CreateBroughtForwardLoss = new("Create brought-forward loss", "POST", Root + "/brought-forward-losses/tax-year/brought-forward-from/{taxYear}", "6.0", Accept, "write:self-assessment", 201, [new("nino"), new("taxYear")], [], true, typeof(CreateBroughtForwardLossRequest), typeof(LossIdResponse), "application/json");
    public static readonly HmrcEndpoint RetrieveBroughtForwardLoss = new("Retrieve brought-forward loss", "GET", Root + "/brought-forward-losses/{lossId}", "6.0", Accept, "read:self-assessment", 200, [new("nino"), new("lossId")], [], false, ResponseType: typeof(BroughtForwardLossResponse));
    public static readonly HmrcEndpoint AmendBroughtForwardLoss = new("Amend brought-forward loss", "PUT", Root + "/brought-forward-losses/{lossId}", "6.0", Accept, "write:self-assessment", 204, [new("nino"), new("lossId")], [], true, typeof(AmendBroughtForwardLossRequest), ContentType: "application/json");
    public static readonly HmrcEndpoint DeleteBroughtForwardLoss = new("Delete brought-forward loss", "DELETE", Root + "/brought-forward-losses/{lossId}", "6.0", Accept, "write:self-assessment", 204, [new("nino"), new("lossId")], [], false);
    public static readonly HmrcEndpoint CreateLossClaim = new("Create loss claim", "POST", Root + "/loss-claims", "6.0", Accept, "write:self-assessment", 201, [new("nino")], [], true, typeof(CreateLossClaimRequest), typeof(ClaimIdResponse), "application/json");
    public static readonly HmrcEndpoint RetrieveLossClaim = new("Retrieve loss claim", "GET", Root + "/loss-claims/{claimId}", "6.0", Accept, "read:self-assessment", 200, [new("nino"), new("claimId")], [], false, ResponseType: typeof(LossClaimResponse));
    public static readonly HmrcEndpoint AmendLossClaim = new("Amend loss claim", "PUT", Root + "/loss-claims/{claimId}", "6.0", Accept, "write:self-assessment", 204, [new("nino"), new("claimId")], [], true, typeof(CreateLossClaimRequest), ContentType: "application/json");
    public static readonly HmrcEndpoint DeleteLossClaim = new("Delete loss claim", "DELETE", Root + "/loss-claims/{claimId}", "6.0", Accept, "write:self-assessment", 204, [new("nino"), new("claimId")], [], false);
    public static IReadOnlyList<HmrcEndpoint> All => [CreateBroughtForwardLoss, RetrieveBroughtForwardLoss, AmendBroughtForwardLoss, DeleteBroughtForwardLoss, CreateLossClaim, RetrieveLossClaim, AmendLossClaim, DeleteLossClaim];
}

public sealed class CreateBroughtForwardLossRequest
{
    [JsonPropertyName("taxYearBroughtForwardFrom")] public required string TaxYearBroughtForwardFrom { get; set; }
    [JsonPropertyName("typeOfLoss")] public required string TypeOfLoss { get; set; }
    [JsonPropertyName("businessId")] public required string BusinessId { get; set; }
    [JsonPropertyName("lossAmount")] public required decimal LossAmount { get; set; }
}

public sealed class AmendBroughtForwardLossRequest
{
    [JsonPropertyName("lossAmount")] public required decimal LossAmount { get; set; }
}

public sealed class LossIdResponse : HmrcResponse
{
    [JsonPropertyName("lossId")] public required string LossId { get; set; }
}

public sealed class BroughtForwardLossResponse : HmrcResponse
{
    [JsonPropertyName("lossId")] public required string LossId { get; set; }
    [JsonPropertyName("taxYearBroughtForwardFrom")] public required string TaxYearBroughtForwardFrom { get; set; }
    [JsonPropertyName("typeOfLoss")] public required string TypeOfLoss { get; set; }
    [JsonPropertyName("businessId")] public required string BusinessId { get; set; }
    [JsonPropertyName("lossAmount")] public required decimal LossAmount { get; set; }
    [JsonPropertyName("lastModified")] public DateTimeOffset? LastModified { get; set; }
}

public sealed class CreateLossClaimRequest
{
    [JsonPropertyName("taxYearClaimedFor")] public required string TaxYearClaimedFor { get; set; }
    [JsonPropertyName("typeOfLoss")] public required string TypeOfLoss { get; set; }
    [JsonPropertyName("typeOfClaim")] public required string TypeOfClaim { get; set; }
    [JsonPropertyName("businessId")] public required string BusinessId { get; set; }
}

public sealed class ClaimIdResponse : HmrcResponse
{
    [JsonPropertyName("claimId")] public required string ClaimId { get; set; }
}

public sealed class LossClaimResponse : HmrcResponse
{
    [JsonPropertyName("claimId")] public required string ClaimId { get; set; }
    [JsonPropertyName("taxYearClaimedFor")] public required string TaxYearClaimedFor { get; set; }
    [JsonPropertyName("typeOfLoss")] public required string TypeOfLoss { get; set; }
    [JsonPropertyName("typeOfClaim")] public required string TypeOfClaim { get; set; }
    [JsonPropertyName("businessId")] public required string BusinessId { get; set; }
    [JsonPropertyName("lastModified")] public DateTimeOffset? LastModified { get; set; }
}
