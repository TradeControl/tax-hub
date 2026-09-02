using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8.Wire2025;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8.Wire2026;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8.Wire2025.List;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8.Wire2026.List;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.Calculations.V8;

public static class CalculationEndpoints
{
    private const string Accept = "application/vnd.hmrc.8.0+json";
    private const string Root = "/individuals/calculations/{nino}/self-assessment/{taxYear}";
    public static readonly HmrcEndpoint Trigger = new("Trigger self-assessment calculation", "POST", Root + "/trigger/{calculationType}", "8.0", Accept, "write:self-assessment", 202, [new("nino"), new("taxYear"), new("calculationType")], [], false, ResponseType: typeof(CalculationIdResponse));
    public static readonly HmrcEndpoint List2025 = new("List self-assessment calculations (2025-26)", "GET", Root, "8.0", Accept, "read:self-assessment", 200, [new("nino"), new("taxYear")], [], false, ResponseType: typeof(CalculationListResponse2025));
    public static readonly HmrcEndpoint List2026 = List2025 with { Operation = "List self-assessment calculations (2026-27 onwards)", ResponseType = typeof(CalculationListResponse2026) };
    public static readonly HmrcEndpoint Retrieve2025 = new("Retrieve self-assessment calculation (2025-26)", "GET", Root + "/{calculationId}", "8.0", Accept, "read:self-assessment", 200, [new("nino"), new("taxYear"), new("calculationId")], [], false, ResponseType: typeof(CalculationResponse2025));
    public static readonly HmrcEndpoint Retrieve2026 = Retrieve2025 with { Operation = "Retrieve self-assessment calculation (2026-27 onwards)", ResponseType = typeof(CalculationResponse2026) };
    public static IReadOnlyList<HmrcEndpoint> All => [Trigger, List2025, List2026, Retrieve2025, Retrieve2026];
}

[JsonConverter(typeof(CalculationTypeConverter))]
public enum CalculationType
{
    InYear,
    IntentToFinalise,
    IntentToAmend
}

public sealed class CalculationTypeConverter : JsonConverter<CalculationType>
{
    public override CalculationType Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "in-year" => CalculationType.InYear,
        "intent-to-finalise" => CalculationType.IntentToFinalise,
        "intent-to-amend" => CalculationType.IntentToAmend,
        _ => throw new JsonException("Unsupported HMRC calculation type.")
    };

    public override void Write(Utf8JsonWriter writer, CalculationType value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        CalculationType.InYear => "in-year",
        CalculationType.IntentToFinalise => "intent-to-finalise",
        CalculationType.IntentToAmend => "intent-to-amend",
        _ => throw new JsonException("Unsupported HMRC calculation type.")
    });
}
