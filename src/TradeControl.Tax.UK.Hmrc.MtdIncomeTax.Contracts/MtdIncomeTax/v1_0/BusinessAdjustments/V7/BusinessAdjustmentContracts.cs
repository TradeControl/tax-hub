using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Cumulative;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessAdjustments.V7;

public static class BusinessAdjustmentEndpoints
{
    private const string Accept = "application/vnd.hmrc.7.0+json";
    private const string Root = "/individuals/self-assessment/adjustable-summary/{nino}";
    public static readonly HmrcEndpoint Trigger = new("Trigger business source adjustable summary", "POST", Root + "/trigger", "7.0", Accept, "write:self-assessment", 200, [new("nino")], [], true, typeof(BsasTriggerRequest), typeof(CalculationIdResponse), "application/json");
    public static readonly HmrcEndpoint Retrieve = new("Retrieve self-employment adjustable summary", "GET", Root + "/self-employment/{calculationId}/{taxYear}", "7.0", Accept, "read:self-assessment", 200, [new("nino"), new("calculationId"), new("taxYear")], [], false, ResponseType: typeof(BsasSummaryResponse));
    public static readonly HmrcEndpoint Adjust = new("Submit self-employment adjustments", "POST", Root + "/self-employment/{calculationId}/adjust/{taxYear}", "7.0", Accept, "write:self-assessment", 200, [new("nino"), new("calculationId"), new("taxYear")], [], true, typeof(BsasAdjustmentRequest), typeof(BsasAdjustmentResponse), "application/json");
    public static IReadOnlyList<HmrcEndpoint> All => [Trigger, Retrieve, Adjust];
}

public sealed class BsasTriggerRequest
{
    [JsonPropertyName("accountingPeriod")] public required BsasAccountingPeriod AccountingPeriod { get; set; }
    [JsonPropertyName("typeOfBusiness")] public string TypeOfBusiness { get; set; } = "self-employment";
    [JsonPropertyName("businessId")] public required string BusinessId { get; set; }
}

public sealed class BsasAccountingPeriod
{
    [JsonPropertyName("startDate")] public required DateOnly StartDate { get; set; }
    [JsonPropertyName("endDate")] public required DateOnly EndDate { get; set; }
}

[JsonDerivedType(typeof(BsasDeltaAdjustments))]
[JsonDerivedType(typeof(BsasZeroAdjustments))]
public abstract class BsasAdjustmentRequest
{
    private protected BsasAdjustmentRequest() { }
}

public sealed class BsasDeltaAdjustments : BsasAdjustmentRequest
{
    [JsonPropertyName("income")] public PeriodIncome? Income { get; set; }
    [JsonPropertyName("expenses")] public PeriodExpenses? Expenses { get; set; }
    [JsonPropertyName("additions")] public PeriodDisallowableExpenses? Additions { get; set; }
}

public sealed class BsasZeroAdjustments : BsasAdjustmentRequest
{
    [JsonPropertyName("zeroAdjustments")] public bool ZeroAdjustments => true;
}

public sealed class BsasSummaryResponse : HmrcResponse
{
    [JsonPropertyName("metadata")] public required BsasMetadata Metadata { get; set; }
    [JsonPropertyName("inputs")] public required BsasInputs Inputs { get; set; }
    [JsonPropertyName("adjustableSummaryCalculation")] public BsasSummaryCalculation? AdjustableSummaryCalculation { get; set; }
    [JsonPropertyName("adjustments")] public BsasDeltaAdjustments? Adjustments { get; set; }
    [JsonPropertyName("adjustedSummaryCalculation")] public BsasSummaryCalculation? AdjustedSummaryCalculation { get; set; }
}

public sealed class BsasMetadata : HmrcResponse
{
    [JsonPropertyName("calculationId")] public required string CalculationId { get; set; }
    [JsonPropertyName("requestedDateTime")] public required DateTimeOffset RequestedDateTime { get; set; }
    [JsonPropertyName("adjustedDateTime")] public DateTimeOffset? AdjustedDateTime { get; set; }
    [JsonPropertyName("nino")] public required string Nino { get; set; }
    [JsonPropertyName("taxYear")] public required string TaxYear { get; set; }
    [JsonPropertyName("summaryStatus")] public required string SummaryStatus { get; set; }
}

public sealed class BsasInputs : HmrcResponse
{
    [JsonPropertyName("typeOfBusiness")] public required string TypeOfBusiness { get; set; }
    [JsonPropertyName("businessId")] public required string BusinessId { get; set; }
    [JsonPropertyName("businessName")] public string? BusinessName { get; set; }
    [JsonPropertyName("accountingPeriodStartDate")] public required DateOnly AccountingPeriodStartDate { get; set; }
    [JsonPropertyName("accountingPeriodEndDate")] public required DateOnly AccountingPeriodEndDate { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("submissionPeriod")] public BsasSubmissionPeriod? SubmissionPeriod { get; set; }
}

public sealed class BsasSubmissionPeriod : HmrcResponse
{
    [JsonPropertyName("submissionId")] public string? SubmissionId { get; set; }
    [JsonPropertyName("startDate")] public required DateOnly StartDate { get; set; }
    [JsonPropertyName("endDate")] public required DateOnly EndDate { get; set; }
    [JsonPropertyName("receivedDateTime")] public DateTimeOffset? ReceivedDateTime { get; set; }
}

public sealed class BsasSummaryCalculation : HmrcResponse
{
    [JsonPropertyName("totalIncome")] public decimal? TotalIncome { get; set; }
    [JsonPropertyName("income")] public PeriodIncome? Income { get; set; }
    [JsonPropertyName("totalExpenses")] public decimal? TotalExpenses { get; set; }
    [JsonPropertyName("expenses")] public PeriodExpenses? Expenses { get; set; }
    [JsonPropertyName("netProfit")] public decimal? NetProfit { get; set; }
    [JsonPropertyName("netLoss")] public decimal? NetLoss { get; set; }
    [JsonPropertyName("totalAdditions")] public decimal? TotalAdditions { get; set; }
    [JsonPropertyName("adjustedProfit")] public decimal? AdjustedProfit { get; set; }
    [JsonPropertyName("adjustedLoss")] public decimal? AdjustedLoss { get; set; }
    [JsonPropertyName("outstandingBusinessIncome")] public decimal? OutstandingBusinessIncome { get; set; }
    [JsonPropertyName("additions")] public BsasAdditions? Additions { get; set; }
    [JsonPropertyName("totalDeductions")] public decimal? TotalDeductions { get; set; }
    [JsonPropertyName("deductions")] public BsasDeductions? Deductions { get; set; }
    [JsonPropertyName("totalAccountingAdjustments")] public decimal? TotalAccountingAdjustments { get; set; }
    [JsonPropertyName("accountingAdjustments")] public BsasAccountingAdjustments? AccountingAdjustments { get; set; }
    [JsonPropertyName("taxableProfit")] public decimal? TaxableProfit { get; set; }
    [JsonPropertyName("taxableLoss")] public decimal? TaxableLoss { get; set; }
}

public sealed class BsasAdditions : PeriodDisallowableExpenses
{
    [JsonPropertyName("balancingChargeOther")] public decimal? BalancingChargeOther { get; set; }
    [JsonPropertyName("balancingChargeBpra")] public decimal? BalancingChargeBpra { get; set; }
    [JsonPropertyName("goodsAndServicesOwnUse")] public decimal? GoodsAndServicesOwnUse { get; set; }
}

public sealed class BsasDeductions : HmrcResponse
{
    [JsonPropertyName("annualInvestmentAllowance")] public decimal? AnnualInvestmentAllowance { get; set; }
    [JsonPropertyName("capitalAllowanceMainPool")] public decimal? CapitalAllowanceMainPool { get; set; }
    [JsonPropertyName("capitalAllowanceSpecialRatePool")] public decimal? CapitalAllowanceSpecialRatePool { get; set; }
    [JsonPropertyName("zeroEmissionGoods")] public decimal? ZeroEmissionGoods { get; set; }
    [JsonPropertyName("businessPremisesRenovationAllowance")] public decimal? BusinessPremisesRenovationAllowance { get; set; }
    [JsonPropertyName("enhancedCapitalAllowance")] public decimal? EnhancedCapitalAllowance { get; set; }
    [JsonPropertyName("allowanceOnSales")] public decimal? AllowanceOnSales { get; set; }
    [JsonPropertyName("capitalAllowanceSingleAssetPool")] public decimal? CapitalAllowanceSingleAssetPool { get; set; }
    [JsonPropertyName("includedNonTaxableProfits")] public decimal? IncludedNonTaxableProfits { get; set; }
    [JsonPropertyName("structuredBuildingAllowance")] public decimal? StructuredBuildingAllowance { get; set; }
    [JsonPropertyName("enhancedStructuredBuildingAllowance")] public decimal? EnhancedStructuredBuildingAllowance { get; set; }
    [JsonPropertyName("zeroEmissionsCarAllowance")] public decimal? ZeroEmissionsCarAllowance { get; set; }
    [JsonPropertyName("tradingIncomeAllowance")] public decimal? TradingIncomeAllowance { get; set; }
}

public sealed class BsasAccountingAdjustments : HmrcResponse
{
    [JsonPropertyName("basisAdjustment")] public decimal? BasisAdjustment { get; set; }
    [JsonPropertyName("accountingAdjustment")] public decimal? AccountingAdjustment { get; set; }
}

public sealed class BsasAdjustmentResponse : HmrcResponse
{
    [JsonPropertyName("calculationId")] public string? CalculationId { get; set; }
    [JsonPropertyName("submittedOn")] public DateTimeOffset? SubmittedOn { get; set; }
}
