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
