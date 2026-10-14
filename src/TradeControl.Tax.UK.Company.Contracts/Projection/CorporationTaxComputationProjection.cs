using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Ct600.V2026;

namespace TradeControl.Tax.UK.Company.Projection;

public static class CorporationTaxComputationCatalog
{
    public const string Namespace = "http://www.hmrc.gov.uk/schemas/ct/comp/2025-01-01";
    public static TaxonomyContract Taxonomy { get; } = new(
        "HMRC-CT-COMPUTATION-2025-V1.0.0", new Uri("http://www.hmrc.gov.uk/schemas/ct/comp/2025-01-01/ct-comp-2025.xsd"),
        new DateOnly(2024, 12, 19), "OfficialHmrcPackagePinned", new Dictionary<string, TaxonomyConcept>
        {
            ["CompanyName"] = C("CompanyName", "CompanyName", "string", "instant"),
            ["TaxReference"] = C("TaxReference", "TaxReference", "string", "instant"),
            ["PeriodOfAccountStartDate"] = C("PeriodOfAccountStartDate", "PeriodOfAccountStartDate", "date", "instant"),
            ["PeriodOfAccountEndDate"] = C("PeriodOfAccountEndDate", "PeriodOfAccountEndDate", "date", "instant"),
            ["StartOfPeriodCoveredByReturn"] = C("StartOfPeriodCoveredByReturn", "StartOfPeriodCoveredByReturn", "date", "instant"),
            ["EndOfPeriodCoveredByReturn"] = C("EndOfPeriodCoveredByReturn", "EndOfPeriodCoveredByReturn", "date", "instant"),
            ["CompanyIsAPartnerInAFirm"] = C("CompanyIsAPartnerInAFirm", "CompanyIsAPartnerInAFirm", "boolean", "duration"),
            ["NameOfProductionSoftware"] = C("NameOfProductionSoftware", "NameOfProductionSoftware", "string", "instant"),
            ["VersionOfProductionSoftware"] = C("VersionOfProductionSoftware", "VersionOfProductionSoftware", "string", "instant"),
            ["AccountsProfit"] = M("AccountsProfit", "ProfitLossPerAccounts"),
            ["AdjustedTradingProfit"] = M("AdjustedTradingProfit", "AdjustedTradingProfitOfThisPeriod"),
            ["CapitalAllowances"] = M("CapitalAllowances", "TotalCapitalAllowances"),
            ["LossesBroughtForward"] = M("LossesBroughtForward", "TradingLossesBroughtForward"),
            ["LossesCurrentPeriod"] = M("LossesCurrentPeriod", "TradingLossesOfThisOrLaterAP"),
            ["LossesUsed"] = M("LossesUsed", "LossesUsedAgainstTradingProfits"),
            ["LossesCarriedForward"] = M("LossesCarriedForward", "BalanceOfLossesBroughtForwardCarriedForward", "instant"),
            ["ChargeableGains"] = M("ChargeableGains", "NetChargeableGains"),
            ["TaxableTotalProfits"] = M("TaxableTotalProfits", "TotalProfitsChargeableToCorporationTax"),
            ["CorporationTaxChargeable"] = M("CorporationTaxChargeable", "CorporationTaxChargeable"),
            ["TaxPayable"] = M("TaxPayable", "TaxPayable")
        });

    private static TaxonomyConcept C(string key, string local, string dataType, string periodType) =>
        new(key, new(Namespace, local), dataType, periodType, false);

    private static TaxonomyConcept M(string key, string local, string periodType = "duration") =>
        new(key, new(Namespace, local), "monetary", periodType, true);
}

public sealed class CorporationTaxComputationProjection
{
    public IxbrlReport Project(CorporationTaxComputation computation, Ct600Return taxReturn)
    {
        var taxonomy = CorporationTaxComputationCatalog.Taxonomy;
        var companyDimensions = new[]
        {
            Dimension("BusinessTypeDimension", "Company"),
            Dimension("DetailedAnalysisDimension", "Item1")
        };
        var taxDimensions = new[]
        {
            Dimension("BusinessTypeDimension", "Company"),
            Dimension("DetailedAnalysisDimension", "Item1"),
            Dimension("TerritoryDimension", "UK")
        };
        var tradeDimensions = new[]
        {
            Dimension("BusinessTypeDimension", "Trade"),
            Dimension("DetailedAnalysisDimension", "Item1"),
            Dimension("LossReformDimension", "Post-lossReform"),
            Dimension("TerritoryDimension", "UK")
        };
        var tradeName = new[]
        {
            new XbrlTypedDimensionMember(
                Name("BusinessNameDimension"),
                Name("BusinessNameDomain"),
                taxReturn.CompanyName)
        };
        var period = new XbrlPeriod.Duration(
            computation.CorporationTaxPeriod.Start,
            computation.CorporationTaxPeriod.End);
        var end = new XbrlPeriod.Instant(computation.CorporationTaxPeriod.End);
        XbrlContext Context(XbrlPeriod contextPeriod, IReadOnlyList<XbrlDimensionMember> dimensions,
            IReadOnlyList<XbrlTypedDimensionMember>? typedDimensions = null) =>
            new("http://www.companieshouse.gov.uk/", taxReturn.CompanyRegistrationNumber,
                contextPeriod, dimensions, typedDimensions);
        var companyDuration = Context(period, companyDimensions);
        var companyInstant = Context(end, companyDimensions);
        var taxDuration = Context(period, taxDimensions);
        var tradeDuration = Context(period, tradeDimensions, tradeName);
        var tradeInstant = Context(end, tradeDimensions, tradeName);
        var gbp = XbrlUnit.Currency("GBP");
        XbrlFact M(string key, decimal value, XbrlContext context) =>
            new(taxonomy.Concepts[key].Name, context, new XbrlValue.Monetary(value), gbp, 2);
        XbrlFact V(string key, XbrlValue value, XbrlContext context) =>
            new(taxonomy.Concepts[key].Name, context, value);
        return new("Corporation Tax computation", taxonomy,
        [
            V("CompanyName", new XbrlValue.Text(taxReturn.CompanyName, null), companyInstant),
            V("TaxReference", new XbrlValue.Text(taxReturn.Utr, null), companyInstant),
            V("PeriodOfAccountStartDate", new XbrlValue.Date(taxReturn.Period.Start), companyInstant),
            V("PeriodOfAccountEndDate", new XbrlValue.Date(taxReturn.Period.End), companyInstant),
            V("StartOfPeriodCoveredByReturn", new XbrlValue.Date(taxReturn.Period.Start), companyInstant),
            V("EndOfPeriodCoveredByReturn", new XbrlValue.Date(taxReturn.Period.End), companyInstant),
            V("CompanyIsAPartnerInAFirm", new XbrlValue.Boolean(false), companyDuration),
            V("NameOfProductionSoftware", new XbrlValue.Text("Trade Control Tax Hub"), companyInstant),
            V("VersionOfProductionSoftware", new XbrlValue.Text("Phase 5.8"), companyInstant),
            M("AccountsProfit", computation.AccountsProfitLossBeforeTax, tradeDuration),
            M("AdjustedTradingProfit", computation.AdjustedTradingProfit, taxDuration),
            M("CapitalAllowances", computation.CapitalAllowances.Total, tradeDuration),
            M("LossesBroughtForward", computation.Losses.BroughtForward, taxDuration),
            M("LossesCurrentPeriod", computation.Losses.CurrentPeriod, taxDuration),
            M("LossesUsed", computation.Losses.Used, tradeDuration),
            M("LossesCarriedForward", computation.Losses.CarriedForward, tradeInstant),
            M("ChargeableGains", computation.ChargeableGains, taxDuration),
            M("TaxableTotalProfits", computation.TaxableTotalProfits, taxDuration),
            M("CorporationTaxChargeable", computation.CorporationTaxChargeable, taxDuration),
            M("TaxPayable", computation.TaxPayable, taxDuration)
        ]);
    }

    private static XbrlDimensionMember Dimension(string dimension, string member) =>
        new(Name(dimension), Name(member));

    private static XbrlQName Name(string localName) =>
        new(CorporationTaxComputationCatalog.Namespace, localName);
}
