using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.Company.Projection;

public static class CorporationTaxComputationCatalog
{
    // HMRC lists the 2025 computation taxonomy as accepted but does not publish a
    // redistributable taxonomy bundle from that list. This derived subset is useful
    // for deterministic semantic fixtures only and is not a submission-ready binding.
    public const string Namespace = "http://www.hmrc.gov.uk/ct/comp/2025-01-01";
    public static TaxonomyContract Taxonomy { get; } = new(
        "HMRC-CT-COMPUTATION-2025", new Uri("http://www.hmrc.gov.uk/ct/comp/2025-01-01/ct-comp-2025-01-01.xsd"),
        new DateOnly(2026, 2, 18), "DerivedCatalogRequiresOfficialValidationAssets", new Dictionary<string, TaxonomyConcept>
        {
            ["AccountsProfit"] = C("AccountsProfit", "ProfitLossBeforeTax"),
            ["AdjustedTradingProfit"] = C("AdjustedTradingProfit", "AdjustedTradingProfit"),
            ["CapitalAllowances"] = C("CapitalAllowances", "CapitalAllowances"),
            ["TaxableTotalProfits"] = C("TaxableTotalProfits", "TaxableTotalProfits"),
            ["CorporationTaxChargeable"] = C("CorporationTaxChargeable", "CorporationTaxChargeable"),
            ["TaxPayable"] = C("TaxPayable", "TaxPayable")
        });

    private static TaxonomyConcept C(string key, string local) => new(key, new(Namespace, local), "monetary", "duration", true);
}

public sealed class CorporationTaxComputationProjection
{
    public IxbrlReport Project(CorporationTaxComputation computation, string companyNumber)
    {
        var taxonomy = CorporationTaxComputationCatalog.Taxonomy;
        var context = new XbrlContext("http://www.companieshouse.gov.uk/", companyNumber,
            new XbrlPeriod.Duration(computation.CorporationTaxPeriod.Start, computation.CorporationTaxPeriod.End));
        var gbp = XbrlUnit.Currency("GBP");
        XbrlFact F(string key, decimal value) => new(taxonomy.Concepts[key].Name, context, new XbrlValue.Monetary(value), gbp, 0);
        return new("Corporation Tax computation", taxonomy,
        [
            F("AccountsProfit", computation.AccountsProfitLossBeforeTax),
            F("AdjustedTradingProfit", computation.AdjustedTradingProfit),
            F("CapitalAllowances", computation.CapitalAllowances.Total),
            F("TaxableTotalProfits", computation.TaxableTotalProfits),
            F("CorporationTaxChargeable", computation.CorporationTaxChargeable),
            F("TaxPayable", computation.TaxPayable)
        ]);
    }
}
