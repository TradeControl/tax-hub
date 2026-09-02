using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Sa.v1_0.Shared;

namespace TradeControl.Tax.UK.Hmrc.Sa.v1_0.SelfEmployment.V5.Annual;

public static class AnnualEndpoints
{
    private const string Path = "/individuals/business/self-employment/{nino}/{businessId}/annual/{taxYear}";
    private const string Accept = "application/vnd.hmrc.5.0+json";
    private static readonly EndpointParameter[] Parameters = [new("nino"), new("businessId"), new("taxYear")];
    public static readonly HmrcEndpoint Put = new("Create or amend annual submission", "PUT", Path, "5.0", Accept, "write:self-assessment", 204, Parameters, [], true, typeof(AnnualSubmission2025), ContentType: "application/json");
    public static readonly HmrcEndpoint Get = new("Retrieve annual submission", "GET", Path, "5.0", Accept, "read:self-assessment", 200, Parameters, [], false, ResponseType: typeof(AnnualSubmission2025));
    public static readonly HmrcEndpoint Delete = new("Delete annual submission", "DELETE", Path, "5.0", Accept, "write:self-assessment", 204, Parameters, [], false);
    public static readonly HmrcEndpoint Put2026Preview = Put with { Operation = "Create or amend annual submission (2026-27 preview)", RequestType = typeof(AnnualSubmission2026Preview), Preview = true };
    public static IReadOnlyList<HmrcEndpoint> Production => [Put, Get, Delete];
}

public class AnnualSubmission2025
{
    [JsonPropertyName("adjustments")] public AnnualAdjustments? Adjustments { get; set; }
    [JsonPropertyName("allowances")] public AnnualAllowances? Allowances { get; set; }
    [JsonPropertyName("nonFinancials")] public AnnualNonFinancials? NonFinancials { get; set; }
}

public sealed class AnnualSubmission2026Preview
{
    [JsonPropertyName("adjustments")] public AnnualAdjustments2026Preview? Adjustments { get; set; }
    [JsonPropertyName("allowances")] public AnnualAllowances? Allowances { get; set; }
    [JsonPropertyName("nonFinancials")] public AnnualNonFinancials? NonFinancials { get; set; }
}

public class AnnualAdjustments
{
    [JsonPropertyName("includedNonTaxableProfits")] public decimal? IncludedNonTaxableProfits { get; set; }
    [JsonPropertyName("basisAdjustment")] public decimal? BasisAdjustment { get; set; }
    [JsonPropertyName("accountingAdjustment")] public decimal? AccountingAdjustment { get; set; }
    [JsonPropertyName("outstandingBusinessIncome")] public decimal? OutstandingBusinessIncome { get; set; }
    [JsonPropertyName("balancingChargeBpra")] public decimal? BalancingChargeBpra { get; set; }
    [JsonPropertyName("balancingChargeOther")] public decimal? BalancingChargeOther { get; set; }
    [JsonPropertyName("goodsAndServicesOwnUse")] public decimal? GoodsAndServicesOwnUse { get; set; }
    [JsonPropertyName("transitionProfitAmount")] public decimal? TransitionProfitAmount { get; set; }
    [JsonPropertyName("transitionProfitAccelerationAmount")] public decimal? TransitionProfitAccelerationAmount { get; set; }
}

public sealed class AnnualAdjustments2026Preview : AnnualAdjustments
{
    [JsonPropertyName("adjustmentToProfitsForClass4")] public decimal? AdjustmentToProfitsForClass4 { get; set; }
}

public sealed class AnnualAllowances
{
    [JsonPropertyName("annualInvestmentAllowance")] public decimal? AnnualInvestmentAllowance { get; set; }
    [JsonPropertyName("capitalAllowanceMainPool")] public decimal? CapitalAllowanceMainPool { get; set; }
    [JsonPropertyName("capitalAllowanceSpecialRatePool")] public decimal? CapitalAllowanceSpecialRatePool { get; set; }
    [JsonPropertyName("businessPremisesRenovationAllowance")] public decimal? BusinessPremisesRenovationAllowance { get; set; }
    [JsonPropertyName("enhancedCapitalAllowance")] public decimal? EnhancedCapitalAllowance { get; set; }
    [JsonPropertyName("allowanceOnSales")] public decimal? AllowanceOnSales { get; set; }
    [JsonPropertyName("capitalAllowanceSingleAssetPool")] public decimal? CapitalAllowanceSingleAssetPool { get; set; }
    [JsonPropertyName("zeroEmissionsCarAllowance")] public decimal? ZeroEmissionsCarAllowance { get; set; }
    [JsonPropertyName("tradingIncomeAllowance")] public decimal? TradingIncomeAllowance { get; set; }
    [JsonPropertyName("structuredBuildingAllowance")] public List<StructuredBuildingAllowance>? StructuredBuildingAllowance { get; set; }
    [JsonPropertyName("enhancedStructuredBuildingAllowance")] public List<StructuredBuildingAllowance>? EnhancedStructuredBuildingAllowance { get; set; }
}

public sealed class StructuredBuildingAllowance
{
    [JsonPropertyName("amount")] public required decimal Amount { get; set; }
    [JsonPropertyName("firstYear")] public StructuredBuildingFirstYear? FirstYear { get; set; }
    [JsonPropertyName("building")] public required StructuredBuilding Building { get; set; }
}

public sealed class StructuredBuildingFirstYear
{
    [JsonPropertyName("qualifyingDate")] public required DateOnly QualifyingDate { get; set; }
    [JsonPropertyName("qualifyingAmountExpenditure")] public required decimal QualifyingAmountExpenditure { get; set; }
}

public sealed class StructuredBuilding
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("postcode")] public required string Postcode { get; set; }
}

public sealed class AnnualNonFinancials
{
    [JsonPropertyName("class4NicsExemptionReason")]
    public string? Class4NicsExemptionReason { get; set; }
}
