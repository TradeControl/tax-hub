using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TradeControl.Tax.UK.Company.Xbrl;

public sealed class IxbrlDocumentBuilder
{
    private static readonly XNamespace Xhtml = "http://www.w3.org/1999/xhtml";
    private static readonly XNamespace Ix = "http://www.xbrl.org/2013/inlineXBRL";
    private static readonly XNamespace Xbrli = "http://www.xbrl.org/2003/instance";
    private static readonly XNamespace Xbrldi = "http://xbrl.org/2006/xbrldi";
    private static readonly XNamespace Link = "http://www.xbrl.org/2003/linkbase";
    private static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    public DocumentArtifact Build(IxbrlReport report, string fileName)
    {
        var contexts = report.Facts.Select(x => x.Context).Distinct().OrderBy(ContextKey).ToArray();
        var units = report.Facts.Where(x => x.Unit is not null).Select(x => x.Unit!).Distinct().OrderBy(x => x.Measure.ToString()).ToArray();
        var contextIds = contexts.Select((x, i) => (x, id: $"c{i + 1}" )).ToDictionary(x => x.x, x => x.id);
        var unitIds = units.Select((x, i) => (x, id: $"u{i + 1}" )).ToDictionary(x => x.x, x => x.id);

        var resources = new XElement(Ix + "resources",
            new XElement(Link + "schemaRef", new XAttribute(Xlink + "type", "simple"), new XAttribute(Xlink + "href", report.Taxonomy.EntryPoint)),
            contexts.Select(x => ContextElement(x, contextIds[x])),
            units.Select(x => UnitElement(x, unitIds[x])));

        var facts = report.Facts.OrderBy(x => x.Concept.ToString()).ThenBy(x => contextIds[x.Context]).Select((fact, i) => FactElement(fact, contextIds[fact.Context], fact.Unit is null ? null : unitIds[fact.Unit], i + 1));
        var root = new XElement(Xhtml + "html",
                new XAttribute(XNamespace.Xmlns + "ix", Ix),
                new XAttribute(XNamespace.Xmlns + "xbrli", Xbrli),
                new XAttribute(XNamespace.Xmlns + "xbrldi", Xbrldi),
                new XAttribute(XNamespace.Xmlns + "link", Link),
                new XAttribute(XNamespace.Xmlns + "xlink", Xlink),
                new XAttribute(XNamespace.Xmlns + "core", Frc2026Catalog.CoreNamespace),
                new XAttribute(XNamespace.Xmlns + "ctcomp", CorporationTaxComputationNamespace),
                new XAttribute(XNamespace.Xmlns + "iso4217", "http://www.xbrl.org/2003/iso4217"),
                new XElement(Xhtml + "head", new XElement(Xhtml + "title", report.Title)),
                new XElement(Xhtml + "body",
                    new XElement(Xhtml + "div", new XAttribute("style", "display:none"), resources),
                    new XElement(Xhtml + "h1", report.Title),
                    new XElement(Xhtml + "table", facts.Select(x => new XElement(Xhtml + "tr", new XElement(Xhtml + "td", x))))));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        var bytes = stream.ToArray();
        return new(fileName, "application/xhtml+xml", bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static XElement ContextElement(XbrlContext context, string id) =>
        new(Xbrli + "context", new XAttribute("id", id),
            new XElement(Xbrli + "entity",
                new XElement(Xbrli + "identifier", new XAttribute("scheme", context.EntityScheme), context.EntityIdentifier),
                context.Dimensions is { Count: > 0 }
                    ? new XElement(Xbrli + "segment", context.Dimensions.OrderBy(x => x.Dimension.ToString()).Select(x =>
                        new XElement(Xbrldi + "explicitMember", new XAttribute("dimension", QName(x.Dimension)), QName(x.Member))))
                    : null),
            new XElement(Xbrli + "period", context.Period switch
            {
                XbrlPeriod.Instant x => new XElement(Xbrli + "instant", x.Date.ToString("yyyy-MM-dd")),
                XbrlPeriod.Duration x => new object[] { new XElement(Xbrli + "startDate", x.Start.ToString("yyyy-MM-dd")), new XElement(Xbrli + "endDate", x.End.ToString("yyyy-MM-dd")) },
                _ => throw new InvalidOperationException()
            }));

    private static XElement UnitElement(XbrlUnit unit, string id) =>
        new(Xbrli + "unit", new XAttribute("id", id), new XElement(Xbrli + "measure", QName(unit.Measure)));

    private static XElement FactElement(XbrlFact fact, string contextId, string? unitId, int index)
    {
        var numeric = fact.Value is XbrlValue.Monetary or XbrlValue.Decimal or XbrlValue.Integer;
        var element = new XElement(Ix + (numeric ? "nonFraction" : "nonNumeric"),
            new XAttribute("id", $"f{index}"), new XAttribute("name", QName(fact.Concept)), new XAttribute("contextRef", contextId), fact.Value.ToInvariantString());
        if (unitId is not null) element.Add(new XAttribute("unitRef", unitId));
        if (fact.Decimals is not null) element.Add(new XAttribute("decimals", fact.Decimals.Value));
        if (fact.Value is XbrlValue.Text text) element.Add(new XAttribute(XNamespace.Xml + "lang", text.Language));
        return element;
    }

    private static string QName(XbrlQName name) => name.NamespaceUri switch
    {
        Frc2026Catalog.CoreNamespace => $"core:{name.LocalName}",
        CorporationTaxComputationNamespace => $"ctcomp:{name.LocalName}",
        "http://www.xbrl.org/2003/iso4217" => $"iso4217:{name.LocalName}",
        "http://www.xbrl.org/2003/instance" => $"xbrli:{name.LocalName}",
        _ => throw new InvalidOperationException($"No deterministic prefix for {name.NamespaceUri}.")
    };

    private const string CorporationTaxComputationNamespace = "http://www.hmrc.gov.uk/ct/comp/2025-01-01";

    private static string ContextKey(XbrlContext x) => $"{x.EntityScheme}|{x.EntityIdentifier}|{x.Period}";
}
