using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Cumulative;

public static class CumulativeEndpoints
{
    private const string Path = "/individuals/business/self-employment/{nino}/{businessId}/cumulative/{taxYear}";
    private const string Accept = "application/vnd.hmrc.5.0+json";
    private static readonly EndpointParameter[] Parameters = [new("nino"), new("businessId"), new("taxYear")];
    public static readonly HmrcEndpoint Put = new("Create or amend cumulative period summary", "PUT", Path, "5.0", Accept, "write:self-assessment", 204, Parameters, [], true, typeof(CumulativeSubmission), ContentType: "application/json");
    public static readonly HmrcEndpoint Get = new("Retrieve cumulative period summary", "GET", Path, "5.0", Accept, "read:self-assessment", 200, Parameters, [], false, ResponseType: typeof(CumulativeSubmission));
    public static IReadOnlyList<HmrcEndpoint> All => [Put, Get];
}

public sealed class CumulativeSubmission
{
    [JsonPropertyName("periodDates")]
    public PeriodDates? PeriodDates { get; set; }

    [JsonPropertyName("periodIncome")]
    public required PeriodIncome PeriodIncome { get; set; }

    [JsonPropertyName("periodExpenses")]
    public required PeriodExpenses PeriodExpenses { get; set; }

    [JsonPropertyName("periodDisallowableExpenses")]
    public PeriodDisallowableExpenses? PeriodDisallowableExpenses { get; set; }
}

public sealed class PeriodDates
{
    [JsonPropertyName("periodStartDate")] public required DateOnly PeriodStartDate { get; set; }
    [JsonPropertyName("periodEndDate")] public required DateOnly PeriodEndDate { get; set; }
}

public sealed class PeriodIncome
{
    [JsonPropertyName("turnover")] public decimal? Turnover { get; set; }
    [JsonPropertyName("other")] public decimal? Other { get; set; }
    [JsonPropertyName("taxTakenOffTradingIncome")] public decimal? TaxTakenOffTradingIncome { get; set; }
}

[JsonConverter(typeof(PeriodExpensesConverter))]
public abstract class PeriodExpenses
{
    private protected PeriodExpenses() { }
}

public sealed class PeriodExpensesConverter : JsonConverter<PeriodExpenses>
{
    public override PeriodExpenses Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var type = document.RootElement.TryGetProperty("consolidatedExpenses", out _)
            ? typeof(ConsolidatedPeriodExpenses)
            : typeof(DetailedPeriodExpenses);
        return (PeriodExpenses)(document.RootElement.Deserialize(type, options)
            ?? throw new JsonException("HMRC periodExpenses could not be deserialized."));
    }

    public override void Write(Utf8JsonWriter writer, PeriodExpenses value, JsonSerializerOptions options)
    {
        switch (value)
        {
            case DetailedPeriodExpenses detailed:
                JsonSerializer.Serialize(writer, detailed, options);
                break;
            case ConsolidatedPeriodExpenses consolidated:
                JsonSerializer.Serialize(writer, consolidated, options);
                break;
            default:
                throw new JsonException("Unsupported HMRC periodExpenses shape.");
        }
    }
}

public sealed class DetailedPeriodExpenses : PeriodExpenses
{
    [JsonPropertyName("costOfGoods")] public decimal? CostOfGoods { get; set; }
    [JsonPropertyName("paymentsToSubcontractors")] public decimal? PaymentsToSubcontractors { get; set; }
    [JsonPropertyName("wagesAndStaffCosts")] public decimal? WagesAndStaffCosts { get; set; }
    [JsonPropertyName("carVanTravelExpenses")] public decimal? CarVanTravelExpenses { get; set; }
    [JsonPropertyName("premisesRunningCosts")] public decimal? PremisesRunningCosts { get; set; }
    [JsonPropertyName("maintenanceCosts")] public decimal? MaintenanceCosts { get; set; }
    [JsonPropertyName("adminCosts")] public decimal? AdminCosts { get; set; }
    [JsonPropertyName("businessEntertainmentCosts")] public decimal? BusinessEntertainmentCosts { get; set; }
    [JsonPropertyName("advertisingCosts")] public decimal? AdvertisingCosts { get; set; }
    [JsonPropertyName("interestOnBankOtherLoans")] public decimal? InterestOnBankOtherLoans { get; set; }
    [JsonPropertyName("financeCharges")] public decimal? FinanceCharges { get; set; }
    [JsonPropertyName("irrecoverableDebts")] public decimal? IrrecoverableDebts { get; set; }
    [JsonPropertyName("professionalFees")] public decimal? ProfessionalFees { get; set; }
    [JsonPropertyName("depreciation")] public decimal? Depreciation { get; set; }
    [JsonPropertyName("otherExpenses")] public decimal? OtherExpenses { get; set; }
}

public sealed class ConsolidatedPeriodExpenses : PeriodExpenses
{
    [JsonPropertyName("consolidatedExpenses")]
    public required decimal ConsolidatedExpenses { get; set; }
}

public class PeriodDisallowableExpenses
{
    [JsonPropertyName("costOfGoodsDisallowable")] public decimal? CostOfGoodsDisallowable { get; set; }
    [JsonPropertyName("paymentsToSubcontractorsDisallowable")] public decimal? PaymentsToSubcontractorsDisallowable { get; set; }
    [JsonPropertyName("wagesAndStaffCostsDisallowable")] public decimal? WagesAndStaffCostsDisallowable { get; set; }
    [JsonPropertyName("carVanTravelExpensesDisallowable")] public decimal? CarVanTravelExpensesDisallowable { get; set; }
    [JsonPropertyName("premisesRunningCostsDisallowable")] public decimal? PremisesRunningCostsDisallowable { get; set; }
    [JsonPropertyName("maintenanceCostsDisallowable")] public decimal? MaintenanceCostsDisallowable { get; set; }
    [JsonPropertyName("adminCostsDisallowable")] public decimal? AdminCostsDisallowable { get; set; }
    [JsonPropertyName("businessEntertainmentCostsDisallowable")] public decimal? BusinessEntertainmentCostsDisallowable { get; set; }
    [JsonPropertyName("advertisingCostsDisallowable")] public decimal? AdvertisingCostsDisallowable { get; set; }
    [JsonPropertyName("interestOnBankOtherLoansDisallowable")] public decimal? InterestOnBankOtherLoansDisallowable { get; set; }
    [JsonPropertyName("financeChargesDisallowable")] public decimal? FinanceChargesDisallowable { get; set; }
    [JsonPropertyName("irrecoverableDebtsDisallowable")] public decimal? IrrecoverableDebtsDisallowable { get; set; }
    [JsonPropertyName("professionalFeesDisallowable")] public decimal? ProfessionalFeesDisallowable { get; set; }
    [JsonPropertyName("depreciationDisallowable")] public decimal? DepreciationDisallowable { get; set; }
    [JsonPropertyName("otherExpensesDisallowable")] public decimal? OtherExpensesDisallowable { get; set; }
}
