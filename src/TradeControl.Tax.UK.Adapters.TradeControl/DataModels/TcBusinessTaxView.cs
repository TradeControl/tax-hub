using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TcBusinessTaxView
{
    public string TaxSourceCode { get; init; } = string.Empty;

    public string TagCode { get; init; } = string.Empty;

    public DateTime PeriodFrom { get; init; }

    public DateTime PeriodTo { get; init; }

    public decimal TaxableAmount { get; init; }
}

public enum TcTaxOrientation
{
    Expense = 0,
    Income = 1
}

public enum TcTaxSupportStatus
{
    Supported,
    Unsupported,
    Invalid
}

public enum TcTaxValidationStatus
{
    Ready,
    Invalid
}

public sealed class TcCumulativeProjection
{
    public string TaxSourceCode { get; init; } = string.Empty;
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public TcTaxValidationStatus ValidationStatus { get; init; }
    public IReadOnlyList<TcCumulativeProjectionValue> Values { get; init; } = [];
    public IReadOnlyList<SourceVersion> Versions { get; init; } = [];
}

public sealed class TcBalanceSheetProjection
{
    public string TaxSourceCode { get; init; } = string.Empty;
    public DateTime AsOfDate { get; init; }
    public DateTime? PeriodStart { get; init; }
    public TcTaxValidationStatus ValidationStatus { get; init; }
    public IReadOnlyList<TcBalanceSheetProjectionValue> Values { get; init; } = [];
    public IReadOnlyList<SourceVersion> Versions { get; init; } = [];
}

public sealed class TcBalanceSheetProjectionValue
{
    public string TagCode { get; init; } = string.Empty;
    public string ValueState { get; init; } = string.Empty;
    public TcTaxSupportStatus SupportStatus { get; init; }
    public decimal? StatutoryAmount { get; init; }
}

public sealed class TcCumulativeProjectionValue
{
    public string TagCode { get; init; } = string.Empty;
    public TcTaxOrientation Orientation { get; init; }
    public TcTaxSupportStatus SupportStatus { get; init; }
    public decimal? StatutoryAmount { get; init; }
}

public sealed class TcCorporationTaxProjection
{
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public DateTime PayOn { get; init; }
    public decimal NetProfit { get; init; }
    public decimal CalculatedTaxDue { get; init; }
    public decimal BusinessTaxAdjustment { get; init; }
    public decimal? BusinessTaxRate { get; init; }
    public bool IsUniformTaxRate { get; init; }
    public decimal StatementTaxDue { get; init; }
    public decimal StatementTaxPaid { get; init; }
    public decimal StatementBalance { get; init; }
    public decimal PreviousLossesCarriedForward { get; init; }
    public decimal LossesCarriedForward { get; init; }
    public IReadOnlyList<SourceVersion> Versions { get; init; } = [];
}

public sealed record TcCorporationTaxLiabilityProjection
{
    public DateTime PeriodStart { get; init; }
    public DateTime PeriodEnd { get; init; }
    public string JurisdictionCode { get; init; } = string.Empty;
    public string TaxSourceCode { get; init; } = string.Empty;
    public string ReportingProfileCode { get; init; } = string.Empty;
    public decimal TaxableResultBeforeRelief { get; init; }
    public decimal OpeningLoss { get; init; }
    public decimal LossCreated { get; init; }
    public decimal LossUsed { get; init; }
    public decimal ClosingLoss { get; init; }
    public decimal TaxableTotalProfits { get; init; }
    public decimal RelevantDistributions { get; init; }
    public decimal AugmentedProfits { get; init; }
    public int AssociatedCompanyCount { get; init; }
    public decimal MainRate { get; init; }
    public decimal SmallProfitsRate { get; init; }
    public decimal MarginalReliefFraction { get; init; }
    public decimal LowerLimit { get; init; }
    public decimal UpperLimit { get; init; }
    public decimal AdjustedLowerLimit { get; init; }
    public decimal AdjustedUpperLimit { get; init; }
    public decimal TaxBeforeMarginalRelief { get; init; }
    public decimal MarginalRelief { get; init; }
    public decimal CorporationTaxChargeable { get; init; }
    public decimal BusinessTaxAdjustment { get; init; }
    public decimal TaxDue { get; init; }
    public string CalculationCode { get; init; } = string.Empty;
    public string? RateFindingCode { get; init; }
    public string? ProfileFindingCode { get; init; }
    public string? KernelFindingCode { get; init; }
    public bool IsAccrualReady { get; init; }
    public bool IsFilingReady { get; init; }
    public IReadOnlyList<SourceVersion> Versions { get; init; } = [];
}
