using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Obligations.V3;

public static class ObligationEndpoints
{
    private const string Version = "3.0";
    private const string Accept = "application/vnd.hmrc.3.0+json";
    private static readonly EndpointParameter[] Path = [new("nino")];
    public static readonly HmrcEndpoint IncomeAndExpenditure = new("Retrieve income and expenditure obligations", "GET", "/obligations/details/{nino}/income-and-expenditure", Version, Accept, "read:self-assessment", 200, Path, [new("typeOfBusiness", false), new("businessId", false), new("fromDate", false), new("toDate", false), new("status", false)], false, ResponseType: typeof(IncomeAndExpenditureObligationsResponse));
    public static readonly HmrcEndpoint FinalDeclaration = new("Retrieve final declaration obligations", "GET", "/obligations/details/{nino}/crystallisation", Version, Accept, "read:self-assessment", 200, Path, [new("taxYear", false), new("status", false)], false, ResponseType: typeof(FinalDeclarationObligationsResponse));
    public static IReadOnlyList<HmrcEndpoint> All => [IncomeAndExpenditure, FinalDeclaration];
}

public sealed record IncomeAndExpenditureQuery(string? TypeOfBusiness = null, string? BusinessId = null, DateOnly? FromDate = null, DateOnly? ToDate = null, string? Status = null);
public sealed record FinalDeclarationQuery(string? TaxYear = null, string? Status = null);

public sealed class IncomeAndExpenditureObligationsResponse : HmrcResponse
{
    [JsonPropertyName("obligations")]
    public required List<BusinessObligations> Obligations { get; set; }
}

public sealed class BusinessObligations : HmrcResponse
{
    [JsonPropertyName("typeOfBusiness")] public required string TypeOfBusiness { get; set; }
    [JsonPropertyName("businessId")] public required string BusinessId { get; set; }
    [JsonPropertyName("obligationDetails")] public required List<ObligationPeriod> ObligationDetails { get; set; }
}

public sealed class FinalDeclarationObligationsResponse : HmrcResponse
{
    [JsonPropertyName("obligations")]
    public required List<ObligationPeriod> Obligations { get; set; }
}

public sealed class ObligationPeriod : HmrcResponse
{
    [JsonPropertyName("periodStartDate")] public required DateOnly PeriodStartDate { get; set; }
    [JsonPropertyName("periodEndDate")] public required DateOnly PeriodEndDate { get; set; }
    [JsonPropertyName("dueDate")] public required DateOnly DueDate { get; set; }
    [JsonPropertyName("receivedDate")] public DateOnly? ReceivedDate { get; set; }
    [JsonPropertyName("status")] public required string Status { get; set; }
}
