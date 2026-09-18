using System.Text;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Accounts.V4;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessAdjustments.V7;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessDetails.V2;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessIncomeSummary.V3;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Calculations.V8;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Finalisation.V8;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Losses.V6;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Losses.V7;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Obligations.V3;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Annual;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Cumulative;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.TaxLiabilityAdjustments.V1;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;

public enum SaRequestShape { AccountingBody, ConfigurationBody, BodylessCommand, BodylessEnquiry }
public enum SaAccountsModeDecision { Supported, Deferred, Unsupported }

public sealed record SaOperationCoverage(
    string OperationId,
    string ContractFamily,
    HmrcEndpoint Descriptor,
    SaRequestShape Shape,
    string RequiredSource,
    SaAccountsModeDecision AccountsMode,
    string PlannedUseCase,
    string WebHarnessRoute,
    bool HasContractFixture,
    bool HasPopulationFixture,
    bool HasHarnessCoverage);

public static class SaOperationCatalog
{
    public static IReadOnlyList<HmrcEndpoint> Production { get; } = BusinessDetailsEndpoints.All
        .Concat(ObligationEndpoints.All).Concat(CumulativeEndpoints.All).Concat(AnnualEndpoints.Production)
        .Concat(BusinessAdjustmentEndpoints.All).Concat(BusinessIncomeSummaryEndpoints.All)
        .Concat(LossV6Endpoints.All).Concat(LossV7Endpoints.All)
        .Concat(TaxLiabilityAdjustmentEndpoints.All).Concat(CalculationEndpoints.All)
        .Concat(FinalisationEndpoints.All).Concat(AccountEndpoints.All).ToArray();

    public static IReadOnlyList<HmrcEndpoint> All { get; } = Production.Append(AnnualEndpoints.Put2026Preview).ToArray();

    public static IReadOnlyList<SaOperationCoverage> Coverage { get; } = All.Select(CreateCoverage).ToArray();

    private static SaOperationCoverage CreateCoverage(HmrcEndpoint endpoint)
    {
        var cumulative = ReferenceEquals(endpoint, CumulativeEndpoints.Put);
        var shape = endpoint.HasRequestBody
            ? cumulative || ReferenceEquals(endpoint, AnnualEndpoints.Put) || ReferenceEquals(endpoint, AnnualEndpoints.Put2026Preview)
                ? SaRequestShape.AccountingBody : SaRequestShape.ConfigurationBody
            : endpoint.Method == "GET" ? SaRequestShape.BodylessEnquiry : SaRequestShape.BodylessCommand;
        var decision = cumulative ? SaAccountsModeDecision.Supported : SaAccountsModeDecision.Deferred;
        var id = $"mtd-it.{Slug(endpoint.Operation)}";
        return new(id, Family(endpoint.PathTemplate), endpoint, shape,
            cumulative ? "cumulative business-income accounting projection"
                : endpoint.HasRequestBody ? "explicit reviewed configuration or future approved accounting source"
                : "typed statutory identifiers and ordered query values",
            decision,
            cumulative ? "PrepareCumulativePeriodSummary" : $"Deferred_{Pascal(endpoint.Operation)}",
            $"/harness/hmrc/mtd-income-tax/{id[7..].Replace('.', '-')}",
            cumulative, cumulative, false);
    }

    private static string Family(string path) => path switch
    {
        var value when value.Contains("/cumulative/", StringComparison.Ordinal) => "Self Employment Business v5 cumulative",
        var value when value.Contains("/annual/", StringComparison.Ordinal) => "Self Employment Business v5 annual",
        var value when value.Contains("business/details", StringComparison.Ordinal) => "Business Details v2",
        var value when value.Contains("obligations", StringComparison.Ordinal) => "Obligations v3",
        var value when value.Contains("business-source-adjustable-summary", StringComparison.Ordinal) => "Business Source Adjustable Summary v7",
        var value when value.Contains("income-summary", StringComparison.Ordinal) => "Business Income Source Summary v3",
        var value when value.Contains("loss", StringComparison.Ordinal) => "Losses",
        var value when value.Contains("tax-liability", StringComparison.Ordinal) => "Tax Liability Adjustments v1",
        var value when value.Contains("calculations", StringComparison.Ordinal) => "Calculations v8",
        var value when value.Contains("final-declaration", StringComparison.Ordinal) || value.Contains("confirm-amendment", StringComparison.Ordinal) => "Finalisation v8",
        var value when value.Contains("/accounts/", StringComparison.Ordinal) => "Accounts v4",
        _ => "MTD Income Tax"
    };

    private static string Slug(string value)
    {
        var result = new StringBuilder(value.Length);
        foreach (var c in value.ToLowerInvariant())
            if (char.IsLetterOrDigit(c)) result.Append(c);
            else if (result.Length > 0 && result[^1] != '-') result.Append('-');
        return result.ToString().Trim('-');
    }

    private static string Pascal(string value) => string.Concat(value.Split([' ', '-', '(', ')'],
        StringSplitOptions.RemoveEmptyEntries).Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
}
