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
    string DecisionReason,
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
        var policy = endpoint.HasRequestBody ? BodyPolicy(endpoint) : BodylessPolicy(endpoint);
        var describedObligations = ReferenceEquals(endpoint, ObligationEndpoints.IncomeAndExpenditure);
        var id = $"mtd-it.{Slug(endpoint.Operation)}";
        return new(id, Family(endpoint.PathTemplate), endpoint, shape,
            policy.RequiredSource,
            policy.Decision,
            policy.Reason,
            policy.PlannedUseCase,
            describedObligations ? "/harness/hmrc/mtd-income-tax/obligations/describe"
                : $"/harness/hmrc/mtd-income-tax/{id[7..].Replace('.', '-')}",
            cumulative || describedObligations, cumulative, describedObligations);
    }

    private static OperationPolicy BodylessPolicy(HmrcEndpoint endpoint)
    {
        if (ReferenceEquals(endpoint, ObligationEndpoints.IncomeAndExpenditure))
            return new(SaAccountsModeDecision.Supported,
                "Approved typed description of the obligations surrounding the cumulative accounting workflow.",
                "typed NINO, optional self-employment identity, paired dates and obligation status",
                "DescribeIncomeAndExpenditureObligations");
        return new(SaAccountsModeDecision.Deferred,
            "This enquiry or command has not yet been classified as necessary for an approved Accounts Mode workflow.",
            "typed statutory identifiers and ordered query values",
            $"Deferred_{Pascal(endpoint.Operation)}");
    }

    private static OperationPolicy BodyPolicy(HmrcEndpoint endpoint)
    {
        if (ReferenceEquals(endpoint, CumulativeEndpoints.Put))
            return new(SaAccountsModeDecision.Supported,
                "Approved Accounts Mode projection with deterministic Trade Control accounting ownership.",
                "cumulative business-income accounting projection",
                "PrepareCumulativePeriodSummary");

        if (ReferenceEquals(endpoint, AnnualEndpoints.Put2026Preview))
            return new(SaAccountsModeDecision.Unsupported,
                "The 2026-27 annual schema is preview-only and cannot be enabled as a production Accounts Mode operation.",
                "future authoritative production contract and reviewed annual filing input",
                "Unsupported_AnnualSubmission2026Preview");

        if (ReferenceEquals(endpoint, AnnualEndpoints.Put))
            return Deferred("Annual fields include elections, allowances and adjustments which require a reviewed filing workflow; accounting data alone is not authoritative.",
                "reviewed annual filing input plus approved accounting projections");

        if (ReferenceEquals(endpoint, BusinessDetailsEndpoints.PutQuarterlyType)
            || ReferenceEquals(endpoint, BusinessDetailsEndpoints.PutAccountingType)
            || ReferenceEquals(endpoint, BusinessDetailsEndpoints.PutPeriodsOfAccount))
            return Deferred("This is taxpayer configuration owned by an explicit administrator or filing workflow, not by accounting SQL.",
                "reviewed business configuration input");

        if (ReferenceEquals(endpoint, BusinessAdjustmentEndpoints.Trigger))
            return Deferred("A BSAS trigger requires an explicit end-of-year workflow choice and accounting-period confirmation.",
                "reviewed end-of-year workflow input");

        if (ReferenceEquals(endpoint, BusinessAdjustmentEndpoints.Adjust))
            return Deferred("BSAS deltas must be reviewed against the HMRC-generated adjustable summary and cannot be inferred from the ledger.",
                "reviewed HMRC BSAS response and operator adjustments");

        if (ReferenceEquals(endpoint, LossV6Endpoints.CreateBroughtForwardLoss)
            || ReferenceEquals(endpoint, LossV6Endpoints.AmendBroughtForwardLoss)
            || ReferenceEquals(endpoint, LossV6Endpoints.CreateLossClaim)
            || ReferenceEquals(endpoint, LossV6Endpoints.AmendLossClaim)
            || ReferenceEquals(endpoint, LossV7Endpoints.Put))
            return Deferred("Loss creation and claim treatment are taxpayer elections requiring reviewed amounts and claim instructions.",
                "reviewed loss and claim input");

        if (ReferenceEquals(endpoint, TaxLiabilityAdjustmentEndpoints.Put))
            return Deferred("A carry-back liability decrease is a reviewed claim outcome, not a value owned by Trade Control accounting.",
                "reviewed tax-liability adjustment input");

        throw new InvalidOperationException($"Body-bearing operation '{endpoint.Operation}' has no explicit Accounts Mode policy.");
    }

    private static OperationPolicy Deferred(string reason, string requiredSource) => new(
        SaAccountsModeDecision.Deferred,
        reason,
        requiredSource,
        "Deferred_PendingReviewedWorkflow");

    private sealed record OperationPolicy(
        SaAccountsModeDecision Decision,
        string Reason,
        string RequiredSource,
        string PlannedUseCase);

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
