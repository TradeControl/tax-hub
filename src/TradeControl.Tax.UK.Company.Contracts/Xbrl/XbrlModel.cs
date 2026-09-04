using System.Globalization;

namespace TradeControl.Tax.UK.Company.Xbrl;

public readonly record struct XbrlQName(string NamespaceUri, string LocalName)
{
    public override string ToString() => $"{{{NamespaceUri}}}{LocalName}";
}

public abstract record XbrlPeriod
{
    public sealed record Instant(DateOnly Date) : XbrlPeriod;
    public sealed record Duration(DateOnly Start, DateOnly End) : XbrlPeriod;
}

public sealed record XbrlDimensionMember(XbrlQName Dimension, XbrlQName Member);

public sealed record XbrlContext(
    string EntityScheme,
    string EntityIdentifier,
    XbrlPeriod Period,
    IReadOnlyList<XbrlDimensionMember>? Dimensions = null);

public sealed record XbrlUnit(XbrlQName Measure)
{
    public static XbrlUnit Currency(string code) =>
        new XbrlUnit(new XbrlQName("http://www.xbrl.org/2003/iso4217", code.ToUpperInvariant()));

    public static XbrlUnit Pure { get; } = new XbrlUnit(new XbrlQName("http://www.xbrl.org/2003/instance", "pure"));
}

public abstract record XbrlValue
{
    public sealed record Monetary(decimal Value) : XbrlValue;
    public sealed record Decimal(decimal Value) : XbrlValue;
    public sealed record Integer(long Value) : XbrlValue;
    public sealed record Boolean(bool Value) : XbrlValue;
    public sealed record Date(DateOnly Value) : XbrlValue;
    public sealed record Text(string Value, string Language = "en") : XbrlValue;

    internal string ToInvariantString() => this switch
    {
        Monetary x => x.Value.ToString("0.############################", CultureInfo.InvariantCulture),
        Decimal x => x.Value.ToString("0.############################", CultureInfo.InvariantCulture),
        Integer x => x.Value.ToString(CultureInfo.InvariantCulture),
        Boolean x => x.Value ? "true" : "false",
        Date x => x.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        Text x => x.Value,
        _ => throw new InvalidOperationException("Unsupported XBRL value.")
    };
}

public sealed record XbrlFact(
    XbrlQName Concept,
    XbrlContext Context,
    XbrlValue Value,
    XbrlUnit? Unit = null,
    int? Decimals = null);

public sealed record TaxonomyConcept(
    string SemanticKey,
    XbrlQName Name,
    string DataType,
    string PeriodType,
    bool Monetary);

public sealed record TaxonomyContract(
    string ReleaseId,
    Uri EntryPoint,
    DateOnly PublishedOn,
    string Status,
    IReadOnlyDictionary<string, TaxonomyConcept> Concepts);

public sealed record IxbrlReport(
    string Title,
    TaxonomyContract Taxonomy,
    IReadOnlyList<XbrlFact> Facts);

public sealed record DocumentArtifact(
    string FileName,
    string MediaType,
    byte[] Content,
    string Sha256);
