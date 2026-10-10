using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TradeControl.Tax.UK.Application.DataProvision;

public enum SubjectBalanceEvidencePosition
{
    Debtor,
    Creditor
}

public sealed record SubjectBalanceEvidenceItem(
    string SubjectCode,
    string SubjectName,
    decimal NativeStatementBalance,
    decimal BusinessBalance,
    SubjectBalanceEvidencePosition Position)
{
    public decimal Magnitude => Math.Abs(BusinessBalance);
}

public sealed record SubjectBalanceEvidenceSourceSummary(
    int DebtorCount,
    int CreditorCount,
    decimal DebtorTotal,
    decimal CreditorTotal);

public sealed record SubjectBalanceEvidenceReconciliation(
    int ConstituentCount,
    int SourceCount,
    decimal DebtorResidual,
    decimal CreditorResidual)
{
    private const decimal Tolerance = 0.00001m;

    public bool IsReconciled => ConstituentCount == SourceCount
        && Math.Abs(DebtorResidual) < Tolerance
        && Math.Abs(CreditorResidual) < Tolerance;
}

public sealed class SubjectBalanceEvidenceSnapshot
{
    public const string ContractVersion = "subject-balances/v1";

    private SubjectBalanceEvidenceSnapshot(
        DateOnly effectiveDate,
        ImmutableArray<SubjectBalanceEvidenceItem> subjects,
        SubjectBalanceEvidenceSourceSummary source,
        SubjectBalanceEvidenceReconciliation reconciliation,
        string sha256)
    {
        EffectiveDate = effectiveDate;
        Subjects = subjects;
        DebtorCount = source.DebtorCount;
        CreditorCount = source.CreditorCount;
        DebtorTotal = source.DebtorTotal;
        CreditorTotal = source.CreditorTotal;
        Reconciliation = reconciliation;
        Sha256 = sha256;
    }

    public DateOnly EffectiveDate { get; }
    public ImmutableArray<SubjectBalanceEvidenceItem> Subjects { get; }
    public int DebtorCount { get; }
    public int CreditorCount { get; }
    public decimal DebtorTotal { get; }
    public decimal CreditorTotal { get; }
    public decimal NetBusinessBalance => DebtorTotal - CreditorTotal;
    public SubjectBalanceEvidenceReconciliation Reconciliation { get; }
    public string Sha256 { get; }
    public string SnapshotToken => Sha256;

    public static SubjectBalanceEvidenceSnapshot Create(
        DateOnly effectiveDate,
        IEnumerable<SubjectBalanceEvidenceItem> subjects,
        SubjectBalanceEvidenceSourceSummary source)
    {
        ArgumentNullException.ThrowIfNull(subjects);
        ArgumentNullException.ThrowIfNull(source);

        if (source.DebtorCount < 0 || source.CreditorCount < 0
            || source.DebtorTotal < 0m || source.CreditorTotal < 0m)
            throw new ArgumentOutOfRangeException(nameof(source), "Source populations and totals cannot be negative.");

        var ordered = subjects
            .Select(Normalize)
            .OrderBy(item => item.SubjectCode, StringComparer.Ordinal)
            .ToImmutableArray();

        if (ordered.Select(item => item.SubjectCode).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new InvalidOperationException("The Subject balance evidence contains a duplicate Subject code.");

        var debtorItems = ordered.Where(item => item.Position == SubjectBalanceEvidencePosition.Debtor).ToArray();
        var creditorItems = ordered.Where(item => item.Position == SubjectBalanceEvidencePosition.Creditor).ToArray();
        var constituentDebtors = debtorItems.Sum(item => item.Magnitude);
        var constituentCreditors = creditorItems.Sum(item => item.Magnitude);
        var reconciliation = new SubjectBalanceEvidenceReconciliation(
            ordered.Length,
            source.DebtorCount + source.CreditorCount,
            source.DebtorTotal - constituentDebtors,
            source.CreditorTotal - constituentCreditors);

        var canonical = Canonical(effectiveDate, source, reconciliation, ordered);
        var sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new(effectiveDate, ordered, source, reconciliation, sha256);
    }

    private static SubjectBalanceEvidenceItem Normalize(SubjectBalanceEvidenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var subjectCode = item.SubjectCode?.Trim() ?? string.Empty;
        var subjectName = item.SubjectName?.Trim() ?? string.Empty;

        if (subjectCode.Length == 0 || subjectName.Length == 0)
            throw new InvalidOperationException("Each Subject balance requires a stable code and display name.");
        if (item.BusinessBalance == 0m)
            throw new InvalidOperationException("Zero Subject balances must not appear in the evidence population.");
        if (item.Position == SubjectBalanceEvidencePosition.Debtor && item.BusinessBalance < 0m
            || item.Position == SubjectBalanceEvidencePosition.Creditor && item.BusinessBalance > 0m)
            throw new InvalidOperationException("A Subject balance does not match its business-polarity population.");
        if (item.NativeStatementBalance != -item.BusinessBalance)
            throw new InvalidOperationException("A Subject statement balance was not converted to business polarity.");

        return item with { SubjectCode = subjectCode, SubjectName = subjectName };
    }

    private static string Canonical(
        DateOnly effectiveDate,
        SubjectBalanceEvidenceSourceSummary source,
        SubjectBalanceEvidenceReconciliation reconciliation,
        ImmutableArray<SubjectBalanceEvidenceItem> subjects)
    {
        var value = new StringBuilder();
        Field(value, ContractVersion);
        Field(value, effectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Field(value, source.DebtorCount.ToString(CultureInfo.InvariantCulture));
        Field(value, source.CreditorCount.ToString(CultureInfo.InvariantCulture));
        Field(value, Decimal(source.DebtorTotal));
        Field(value, Decimal(source.CreditorTotal));
        Field(value, reconciliation.ConstituentCount.ToString(CultureInfo.InvariantCulture));
        Field(value, reconciliation.SourceCount.ToString(CultureInfo.InvariantCulture));
        Field(value, Decimal(reconciliation.DebtorResidual));
        Field(value, Decimal(reconciliation.CreditorResidual));

        foreach (var item in subjects)
        {
            Field(value, item.SubjectCode);
            Field(value, item.SubjectName);
            Field(value, ((int)item.Position).ToString(CultureInfo.InvariantCulture));
            Field(value, Decimal(item.NativeStatementBalance));
            Field(value, Decimal(item.BusinessBalance));
        }

        return value.ToString();
    }

    private static string Decimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static void Field(StringBuilder target, string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        target.Append(byteCount.ToString(CultureInfo.InvariantCulture))
            .Append(':')
            .Append(value)
            .Append('\n');
    }
}
