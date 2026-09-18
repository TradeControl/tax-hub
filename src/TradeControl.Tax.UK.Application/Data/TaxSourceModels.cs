namespace TradeControl.Tax.Data;

public enum TaxLegalForm { Company, SoleTrader, Partnership, Other }
public enum TaxSubjectKind { Person, Organisation }
public enum TaxPeriodKind { Vat, TaxYear, Cumulative, Accounting }
public enum TaxFactKind { Income, Expense }
public enum TaxValueState { Present, ExplicitZero, Absent, Unsupported, Invalid, NotApplicable }
public enum SourceFindingSeverity { Information, Warning, Error }
public enum ReadinessScope { Source, Accounting, Mapping }

public readonly record struct SourceKey
{
    public SourceKey(string value)
    {
        Value = IdentifierRules.Require(value, "source key", nameof(value));
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct TaxSourceCode
{
    public TaxSourceCode(string value)
    {
        Value = IdentifierRules.Require(value, "tax-source code", nameof(value));
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public readonly record struct BusinessId
{
    public BusinessId(string value)
    {
        Value = IdentifierRules.Require(value, "business identifier", nameof(value));
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record TaxSubject(
    string SubjectCode,
    string DisplayName,
    TaxSubjectKind SubjectKind,
    TaxLegalForm LegalForm,
    string JurisdictionCode,
    string CurrencyCode);

public sealed record TaxReportingPeriod
{
    public TaxReportingPeriod(DateOnly start, DateOnly end, TaxPeriodKind kind, string stableKey)
    {
        if (end < start) throw new ArgumentException("A reporting period cannot end before it starts.");
        if (string.IsNullOrWhiteSpace(stableKey)) throw new ArgumentException("A stable period key is required.", nameof(stableKey));
        Start = start;
        End = end;
        Kind = kind;
        StableKey = stableKey.Trim();
    }
    public DateOnly Start { get; }
    public DateOnly End { get; }
    public TaxPeriodKind Kind { get; }
    public string StableKey { get; }
}

public sealed record FactProvenance(
    string SourceSystem,
    string DatasetKey,
    string SemanticKey,
    string? MappingKey = null);

public sealed record DatasetProvenance(
    string SourceSystem,
    string DatasetKey,
    string ProjectionVersion,
    DateTimeOffset CapturedAt,
    string SnapshotToken,
    IReadOnlyList<FactProvenance> Facts);

public sealed class TaxValue<T>
{
    private TaxValue(TaxValueState state, T? value, string? reason)
    {
        State = state;
        Value = value;
        Reason = reason;
    }

    public TaxValueState State { get; }
    public T? Value { get; }
    public string? Reason { get; }
    public bool HasValue => State is TaxValueState.Present or TaxValueState.ExplicitZero;

    public static TaxValue<T> Present(T value) => new(TaxValueState.Present, value, null);
    public static TaxValue<T> ExplicitZero(T value)
    {
        if (!EqualityComparer<T>.Default.Equals(value, default!))
            throw new ArgumentException("An explicit-zero value must equal the type default.", nameof(value));
        return new(TaxValueState.ExplicitZero, value, null);
    }
    public static TaxValue<T> Absent(string? reason = null) => new(TaxValueState.Absent, default, reason);
    public static TaxValue<T> Unsupported(string reason) => WithoutValue(TaxValueState.Unsupported, reason);
    public static TaxValue<T> Invalid(string reason) => WithoutValue(TaxValueState.Invalid, reason);
    public static TaxValue<T> NotApplicable(string? reason = null) => new(TaxValueState.NotApplicable, default, reason);

    private static TaxValue<T> WithoutValue(TaxValueState state, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("A reason is required.", nameof(reason));
        return new(state, default, reason.Trim());
    }
}

public sealed record TaxFact<T>(
    string StableKey,
    string DisplayLabel,
    TaxValue<T> Value,
    FactProvenance Provenance);

public sealed record VatReturnSource(
    TaxSubject Subject,
    TaxReportingPeriod Period,
    TaxFact<decimal> VatDueSales,
    TaxFact<decimal> VatDueAcquisitions,
    TaxFact<decimal> VatAdjustment,
    TaxFact<decimal> TotalVatDue,
    TaxFact<decimal> VatReclaimedCurrentPeriod,
    TaxFact<decimal> NetVatDue,
    TaxFact<decimal> TotalValueSalesExVat,
    TaxFact<decimal> TotalValuePurchasesExVat,
    TaxFact<decimal> TotalValueGoodsSuppliedExVat,
    TaxFact<decimal> TotalAcquisitionsExVat,
    DatasetProvenance Provenance);

public readonly record struct BusinessFactKey
{
    public BusinessFactKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A business fact key is required.", nameof(value));
        Value = value.Trim();
    }
    public string Value { get; }
    public override string ToString() => Value;
}

public sealed record BusinessIncomeFact(
    BusinessFactKey Key,
    string DisplayLabel,
    TaxFactKind Kind,
    TaxValue<decimal> Amount,
    FactProvenance Provenance);

public sealed record BusinessIncomeSource(
    TaxSubject Subject,
    BusinessId BusinessId,
    TaxSourceCode TaxSourceCode,
    TaxReportingPeriod Period,
    IReadOnlyList<BusinessIncomeFact> Facts,
    DatasetProvenance Provenance);

public sealed record SourceFinding(
    ReadinessScope Scope,
    SourceFindingSeverity Severity,
    string Code,
    string Message,
    string? StableFactKey = null);

public sealed record SourceReadiness(IReadOnlyList<SourceFinding> Findings)
{
    public bool IsReady => Findings.All(x => x.Severity != SourceFindingSeverity.Error);
}

internal static class IdentifierRules
{
    public static string Require(string value, string description, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"A {description} is required.", parameterName);
        var trimmed = value.Trim();
        if (trimmed.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
            throw new ArgumentException($"The {description} contains unsafe characters.", parameterName);
        return trimmed;
    }
}
