using System.Globalization;
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
    private static readonly XNamespace Ixt = "http://www.xbrl.org/inlineXBRL/transformation/2011-07-31";

    public DocumentArtifact Build(IxbrlReport report, string fileName)
    {
        var contexts = report.Facts.Select(x => x.Context).Distinct().OrderBy(ContextKey).ToArray();
        var units = report.Facts.Where(x => x.Unit is not null).Select(x => x.Unit!).Distinct().OrderBy(x => x.Measure.ToString()).ToArray();
        var contextIds = contexts.Select((x, i) => (x, id: $"c{i + 1}" )).ToDictionary(x => x.x, x => x.id);
        var unitIds = units.Select((x, i) => (x, id: $"u{i + 1}" )).ToDictionary(x => x.x, x => x.id);

        var references = new XElement(Ix + "references",
            new XElement(Link + "schemaRef", new XAttribute(Xlink + "type", "simple"), new XAttribute(Xlink + "href", report.Taxonomy.EntryPoint)));
        var resources = new XElement(Ix + "resources",
            contexts.Select(x => ContextElement(x, contextIds[x])),
            units.Select(x => UnitElement(x, unitIds[x])));

        var orderedFacts = report.Facts.OrderBy(x => x.Concept.ToString()).ThenBy(x => contextIds[x.Context]).ToArray();
        var factIndexes = orderedFacts.Select((fact, index) => (fact, index: index + 1)).ToDictionary(x => x.fact, x => x.index);
        XElement RenderFact(XbrlFact fact) => FactElement(fact, contextIds[fact.Context],
            fact.Unit is null ? null : unitIds[fact.Unit], factIndexes[fact]);
        var body = report.Presentation?.Kind == IxbrlPresentationKind.StatutoryAccounts
            ? StatutoryAccountsBody(report, orderedFacts, RenderFact)
            : new XElement(Xhtml + "table", orderedFacts.Select(x =>
                new XElement(Xhtml + "tr", new XElement(Xhtml + "td", RenderFact(x)))));
        var root = new XElement(Xhtml + "html",
                new XAttribute(XNamespace.Xmlns + "ix", Ix),
                new XAttribute(XNamespace.Xmlns + "xbrli", Xbrli),
                new XAttribute(XNamespace.Xmlns + "xbrldi", Xbrldi),
                new XAttribute(XNamespace.Xmlns + "link", Link),
                new XAttribute(XNamespace.Xmlns + "xlink", Xlink),
                new XAttribute(XNamespace.Xmlns + "ixt", Ixt),
                new XAttribute(XNamespace.Xmlns + "core", Frc2026Catalog.CoreNamespace),
                new XAttribute(XNamespace.Xmlns + "bus", Frc2026Catalog.BusinessNamespace),
                new XAttribute(XNamespace.Xmlns + "direp", Frc2026Catalog.DirectorsReportNamespace),
                // Retain the unused historical declaration on non-CT documents so the
                // established statutory-accounts bytes remain immutable. CT documents
                // bind the authoritative HMRC 2025 namespace used by their fact QNames.
                new XAttribute(XNamespace.Xmlns + "ctcomp",
                    report.Taxonomy.ReleaseId.StartsWith("HMRC-CT-COMPUTATION-2025", StringComparison.Ordinal)
                        ? CorporationTaxComputationNamespace
                        : LegacyCorporationTaxComputationNamespace),
                new XAttribute(XNamespace.Xmlns + "iso4217", "http://www.xbrl.org/2003/iso4217"),
                new XElement(Xhtml + "head", new XElement(Xhtml + "title", report.Title),
                    new XElement(Xhtml + "style", new XAttribute("type", "text/css"), "body{font-family:Arial,sans-serif;max-width:900px;margin:2rem auto;color:#222}h1,h2{text-align:center}.identity{text-align:center}.accounts{width:100%;border-collapse:collapse;margin:1.5rem 0}.accounts th,.accounts td{padding:.4rem .6rem;border-bottom:1px solid #ddd}.accounts th:first-child,.accounts td:first-child{text-align:left}.accounts th:not(:first-child),.accounts td:not(:first-child){text-align:right}.total td{font-weight:bold;border-top:1px solid #222}.statements p{margin:.7rem 0}.signature{margin-top:2rem}.filing-information{font-size:.85rem;color:#444}")),
                new XElement(Xhtml + "body",
                    new XElement(Xhtml + "div", new XAttribute("style", "display:none"),
                        new XElement(Ix + "header", references, resources)),
                    body));
        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        var bytes = stream.ToArray();
        return new(fileName, "application/xhtml+xml", bytes, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static XElement StatutoryAccountsBody(IxbrlReport report, IReadOnlyList<XbrlFact> facts,
        Func<XbrlFact, XElement> render)
    {
        var used = new HashSet<XbrlFact>();
        XbrlQName Concept(string key) => report.Taxonomy.Concepts[key].Name;
        XbrlFact? Find(string key, Func<XbrlFact, bool>? predicate = null)
        {
            var name = Concept(key);
            var fact = facts.FirstOrDefault(x => x.Concept == name && (predicate is null || predicate(x)) && !used.Contains(x));
            if (fact is not null) used.Add(fact);
            return fact;
        }
        object Tagged(string key, Func<XbrlFact, bool>? predicate = null)
        {
            var fact = Find(key, predicate);
            return fact is null ? string.Empty : render(fact);
        }
        object TaggedAmount(string key, Func<XbrlFact, bool>? predicate = null)
        {
            var fact = Find(key, predicate);
            if (fact is null) return string.Empty;

            var element = render(fact);
            if (fact.Value is not XbrlValue.Monetary monetary) return element;

            element.Value = Math.Abs(monetary.Value).ToString("#,##0.00", CultureInfo.InvariantCulture);
            element.Add(new XAttribute("format", "ixt:numdotdecimal"));
            if (monetary.Value >= 0) return element;

            element.Add(new XAttribute("sign", "-"));
            return new object[] { "(", element, ")" };
        }
        static bool InstantOn(XbrlFact fact, DateOnly date) => fact.Context.Period is XbrlPeriod.Instant instant && instant.Date == date;
        static bool DurationEndingOn(XbrlFact fact, DateOnly date) => fact.Context.Period is XbrlPeriod.Duration duration && duration.End == date;

        var balanceSheetDateFact = facts.Single(x => x.Concept == Concept("BalanceSheetDate"));
        var currentDate = ((XbrlValue.Date)balanceSheetDateFact.Value).Value;
        var comparativeDate = facts
            .Where(x => x.Value is XbrlValue.Monetary && x.Context.Period is XbrlPeriod.Instant)
            .Select(x => ((XbrlPeriod.Instant)x.Context.Period).Date)
            .Where(x => x != currentDate).Distinct().OrderByDescending(x => x).Cast<DateOnly?>().FirstOrDefault();
        var presentation = report.Presentation!;

        XElement BalanceRow(string label, string key, bool total = false) =>
            new(Xhtml + "tr", total ? new XAttribute("class", "total") : null,
                new XElement(Xhtml + "td", label),
                new XElement(Xhtml + "td", TaggedAmount(key, x => InstantOn(x, currentDate))),
                comparativeDate is null ? null : new XElement(Xhtml + "td", TaggedAmount(key, x => InstantOn(x, comparativeDate.Value))));
        XElement IncomeRow(string label, string key, bool total = false) =>
            new(Xhtml + "tr", total ? new XAttribute("class", "total") : null,
                new XElement(Xhtml + "td", label),
                new XElement(Xhtml + "td", TaggedAmount(key, x => DurationEndingOn(x, currentDate))),
                comparativeDate is null ? null : new XElement(Xhtml + "td", TaggedAmount(key, x => DurationEndingOn(x, comparativeDate.Value))));
        XElement FilingClassification(string label, string key, string dimension, string defaultValue)
        {
            var fact = Find(key);
            if (fact is null) return new XElement(Xhtml + "li", label, ": unavailable");

            var member = fact.Context.Dimensions?
                .FirstOrDefault(item => item.Dimension.LocalName == dimension)?.Member.LocalName;
            var display = (dimension, member) switch
            {
                ("AccountsStatusDimension", "AuditExempt-NoAccountantsReport") =>
                    "Unaudited — audit exempt, no accountants' report",
                ("AccountsTypeDimension", "FilletedAccounts") => "Filleted accounts",
                ("AccountsTypeDimension", "FullAccounts") => "Full accounts",
                ("AccountingStandardsDimension", "Micro-entities") => "FRS 105 micro-entities",
                ("EntityTradingStatusDimension", "EntityHasNeverTraded") => "Never traded",
                ("EntityTradingStatusDimension", "EntityNoLongerTradingButTradedInPast") =>
                    "No longer trading",
                _ => defaultValue
            };

            // FRC fixedItemType facts have a deliberately empty fact body: their value is carried
            // by the context dimension (or its published default). Keep the fact unchanged and add
            // a human-readable interpretation alongside it.
            return new XElement(Xhtml + "li", label, ": ", render(fact), display);
        }

        var companyName = Tagged("CompanyName");
        var companyNumber = Tagged("CompanyNumber");
        used.Add(balanceSheetDateFact);
        var balanceSheetDate = render(balanceSheetDateFact);
        var content = new List<object>
        {
            new XElement(Xhtml + "h1", companyName),
            new XElement(Xhtml + "p", new XAttribute("class", "identity"), "Registered number: ", companyNumber),
            new XElement(Xhtml + "h2", presentation.Filleted ? "Filleted micro-entity accounts" : "Micro-entity accounts"),
            new XElement(Xhtml + "h2", "Balance sheet as at ", balanceSheetDate),
            new XElement(Xhtml + "table", new XAttribute("class", "accounts"),
                new XElement(Xhtml + "thead", new XElement(Xhtml + "tr",
                    new XElement(Xhtml + "th", string.Empty),
                    new XElement(Xhtml + "th", currentDate.Year, " ", CurrencyUnitLabel(presentation.Currency)),
                    comparativeDate is null ? null : new XElement(Xhtml + "th", comparativeDate.Value.Year, " ", CurrencyUnitLabel(presentation.Currency)))),
                new XElement(Xhtml + "tbody",
                    BalanceRow("Fixed assets", "FixedAssets"),
                    BalanceRow("Current assets", "CurrentAssets"),
                    BalanceRow("Prepayments and accrued income", "Prepayments"),
                    BalanceRow("Creditors: amounts falling due within one year", "CreditorsWithinOneYear"),
                    BalanceRow("Net current assets (liabilities)", "NetCurrentAssets", true),
                    BalanceRow("Total assets less current liabilities", "TotalAssetsLessCurrentLiabilities", true),
                    BalanceRow("Creditors: amounts falling due after one year", "CreditorsAfterOneYear"),
                    BalanceRow("Provisions for liabilities", "Provisions"),
                    BalanceRow("Accrued liabilities and deferred income", "AccrualsDeferredIncome"),
                    BalanceRow("Net assets (liabilities)", "NetAssets", true),
                    BalanceRow("Capital and reserves", "CapitalAndReserves", true)))
        };

        if (!presentation.Filleted)
            content.Add(new XElement(Xhtml + "div", new XElement(Xhtml + "h2", "Income statement"),
                new XElement(Xhtml + "table", new XAttribute("class", "accounts"),
                    new XElement(Xhtml + "tbody", IncomeRow("Turnover", "Turnover"), IncomeRow("Other income", "OtherIncome"),
                        IncomeRow("Cost of sales", "CostOfSales"), IncomeRow("Administrative expenses", "AdministrativeExpenses"),
                        IncomeRow("Tax on profit or loss", "TaxOnProfit"), IncomeRow("Profit (loss) for the period", "ProfitLoss", true)))));

        content.Add(new XElement(Xhtml + "div", new XAttribute("class", "statements"),
            new XElement(Xhtml + "h2", "Statements"),
            new XElement(Xhtml + "p", Tagged("MicroEntityProvisions")),
            new XElement(Xhtml + "p", Tagged("Section477")),
            new XElement(Xhtml + "p", Tagged("MembersNoAudit")),
            new XElement(Xhtml + "p", Tagged("DirectorsResponsibilities"))));
        content.Add(new XElement(Xhtml + "div", new XElement(Xhtml + "h2", "Notes"),
            new XElement(Xhtml + "p", new XElement(Xhtml + "strong", "Principal activity: "), Tagged("PrincipalActivity")),
            new XElement(Xhtml + "p", new XElement(Xhtml + "strong", "Average number of employees: "), Tagged("AverageEmployees"))));
        content.Add(new XElement(Xhtml + "div", new XAttribute("class", "signature"),
            new XElement(Xhtml + "p", "Approved by the board on ", Tagged("AccountsApprovedOn")),
            new XElement(Xhtml + "p", Tagged("DirectorSigning"), presentation.SigningDirectorName, ", Director")));

        var metadata = new[]
        {
            new XElement(Xhtml + "li", "Period start: ", Tagged("PeriodStart")),
            new XElement(Xhtml + "li", "Period end: ", Tagged("PeriodEnd")),
            FilingClassification("Accounts status", "AccountsStatus", "AccountsStatusDimension",
                "Supported unaudited accounts profile"),
            FilingClassification("Accounts type", "AccountsType", "AccountsTypeDimension",
                presentation.Filleted ? "Filleted accounts" : "Full accounts"),
            FilingClassification("Accounting standard", "AccountingStandards", "AccountingStandardsDimension",
                "FRS 105 micro-entities"),
            FilingClassification("Trading status", "TradingStatus", "EntityTradingStatusDimension", "Trading"),
            new XElement(Xhtml + "li", "Dormant company: ", Tagged("DormantStatus"))
        };
        content.Add(new XElement(Xhtml + "div", new XAttribute("class", "filing-information"),
            new XElement(Xhtml + "h2", "Accounts information"),
            new XElement(Xhtml + "p", "These classifications are derived from the supported filing profile and encoded in the XBRL contexts."),
            new XElement(Xhtml + "ul", metadata)));
        var remaining = facts.Where(x => !used.Contains(x)).ToArray();
        if (remaining.Length > 0)
            content.Add(new XElement(Xhtml + "div", new XAttribute("class", "filing-information"),
                new XElement(Xhtml + "h2", "Additional filing information"),
                new XElement(Xhtml + "ul", remaining.Select(x => new XElement(Xhtml + "li", x.Concept.LocalName, ": ", render(x))))));

        return new XElement(Xhtml + "div", new XAttribute("class", "accounts-document"), content);
    }

    private static XElement ContextElement(XbrlContext context, string id) =>
        new(Xbrli + "context", new XAttribute("id", id),
            new XElement(Xbrli + "entity",
                new XElement(Xbrli + "identifier", new XAttribute("scheme", context.EntityScheme), context.EntityIdentifier),
                context.Dimensions is { Count: > 0 } || context.TypedDimensions is { Count: > 0 }
                    ? new XElement(Xbrli + "segment",
                        context.Dimensions?.OrderBy(x => x.Dimension.ToString()).Select(x =>
                            new XElement(Xbrldi + "explicitMember", new XAttribute("dimension", QName(x.Dimension)), QName(x.Member))),
                        context.TypedDimensions?.OrderBy(x => x.Dimension.ToString()).Select(x =>
                            new XElement(Xbrldi + "typedMember", new XAttribute("dimension", QName(x.Dimension)),
                                new XElement(XName.Get(x.Domain.LocalName, x.Domain.NamespaceUri), x.Value))))
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
        if (fact.Value is XbrlValue.Text { Language: { Length: > 0 } language })
            element.Add(new XAttribute(XNamespace.Xml + "lang", language));
        return element;
    }

    private static string QName(XbrlQName name) => name.NamespaceUri switch
    {
        Frc2026Catalog.CoreNamespace => $"core:{name.LocalName}",
        Frc2026Catalog.BusinessNamespace => $"bus:{name.LocalName}",
        Frc2026Catalog.DirectorsReportNamespace => $"direp:{name.LocalName}",
        CorporationTaxComputationNamespace => $"ctcomp:{name.LocalName}",
        "http://www.xbrl.org/2003/iso4217" => $"iso4217:{name.LocalName}",
        "http://www.xbrl.org/2003/instance" => $"xbrli:{name.LocalName}",
        _ => throw new InvalidOperationException($"No deterministic prefix for {name.NamespaceUri}.")
    };

    private const string CorporationTaxComputationNamespace = "http://www.hmrc.gov.uk/schemas/ct/comp/2025-01-01";
    private const string LegacyCorporationTaxComputationNamespace = "http://www.hmrc.gov.uk/ct/comp/2025-01-01";

    private static string CurrencyUnitLabel(string currency) => currency.ToUpperInvariant() switch
    {
        "GBP" => "£'s",
        _ => currency.ToUpperInvariant()
    };

    private static string ContextKey(XbrlContext x) => $"{x.EntityScheme}|{x.EntityIdentifier}|{x.Period}";
}
