using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;

public sealed record EndpointParameter(string Name, bool Required = true);

public sealed record HmrcEndpoint(
    string Operation,
    string Method,
    string PathTemplate,
    string ApiVersion,
    string Accept,
    string OAuthScope,
    int SuccessStatusCode,
    IReadOnlyList<EndpointParameter> PathParameters,
    IReadOnlyList<EndpointParameter> QueryParameters,
    bool HasRequestBody,
    Type? RequestType = null,
    Type? ResponseType = null,
    string? ContentType = null,
    bool Preview = false);

public static class SaJson
{
    public static JsonSerializerOptions CreateOptions() => new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = null,
        WriteIndented = true
    };
}

public abstract class HmrcResponse
{
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}

public sealed class HmrcErrorResponse : HmrcResponse
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("errors")]
    public List<HmrcErrorDetail>? Errors { get; set; }
}

public sealed class HmrcErrorDetail
{
    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public sealed class CalculationIdResponse : HmrcResponse
{
    [JsonPropertyName("calculationId")]
    public required string CalculationId { get; set; }
}
