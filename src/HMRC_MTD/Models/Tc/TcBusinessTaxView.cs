namespace TradeControl.Tax.UK.Models.Tc;

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
}

public sealed class TcCumulativeProjectionValue
{
    public string TagCode { get; init; } = string.Empty;
    public TcTaxOrientation Orientation { get; init; }
    public TcTaxSupportStatus SupportStatus { get; init; }
    public decimal? StatutoryAmount { get; init; }
}
