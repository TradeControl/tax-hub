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
            ["RelevantDistributions"] = M("RelevantDistributions", "ExemptABGHDistributions"),
            ["MarginalRelief"] = M("MarginalRelief", "MarginalRateReliefForRingFenceTradesPayable"),
            ["AssociatedCompanies"] = C("AssociatedCompanies", "NumberOfAssociatedCompaniesInThisPeriod", "integer", "duration"),
            ["AssociatedCompaniesFY1"] = C("AssociatedCompaniesFY1", "NumberOfAssociatedCompaniesInFY1", "integer", "duration"),
            ["AssociatedCompaniesFY2"] = C("AssociatedCompaniesFY2", "NumberOfAssociatedCompaniesInFY2", "integer", "duration"),
            ["FinancialYear1"] = C("FinancialYear1", "FinancialYear1CoveredByTheReturn", "gYear", "duration"),
            ["FinancialYear2"] = C("FinancialYear2", "FinancialYear2CoveredByTheReturn", "gYear", "duration"),
            ["FY1ProfitFirstRate"] = M("FY1ProfitFirstRate", "FY1AmountOfProfitChargeableAtFirstRate"),
            ["FY1ProfitSecondRate"] = M("FY1ProfitSecondRate", "FY1AmountOfProfitChargeableAtSecondRate"),
            ["FY2ProfitFirstRate"] = M("FY2ProfitFirstRate", "FY2AmountOfProfitChargeableAtFirstRate"),
            ["FY2ProfitSecondRate"] = M("FY2ProfitSecondRate", "FY2AmountOfProfitChargeableAtSecondRate"),
            ["FY1FirstRate"] = C("FY1FirstRate", "FY1FirstRateOfTax", "decimal", "duration"),
            ["FY1SecondRate"] = C("FY1SecondRate", "FY1SecondRateOfTax", "decimal", "duration"),
            ["FY2FirstRate"] = C("FY2FirstRate", "FY2FirstRateOfTax", "decimal", "duration"),
            ["FY2SecondRate"] = C("FY2SecondRate", "FY2SecondRateOfTax", "decimal", "duration"),
            ["FY1TaxFirstRate"] = M("FY1TaxFirstRate", "FY1TaxAtFirstRate"),
            ["FY1TaxSecondRate"] = M("FY1TaxSecondRate", "FY1TaxAtSecondRate"),
            ["FY2TaxFirstRate"] = M("FY2TaxFirstRate", "FY2TaxAtFirstRate"),
            ["FY2TaxSecondRate"] = M("FY2TaxSecondRate", "FY2TaxAtSecondRate"),
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
        XbrlFact N(string key, XbrlValue value, XbrlContext context, int decimals) =>
            new(taxonomy.Concepts[key].Name, context, value, XbrlUnit.Pure, decimals);
        var facts = new List<XbrlFact>
        {
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
            M("TaxPayable", computation.TaxPayable, taxDuration),
            M("RelevantDistributions", computation.Liability.RelevantDistributions, taxDuration),
            N("AssociatedCompanies", new XbrlValue.Integer(computation.Liability.AssociatedCompanyCount), taxDuration, 0)
        };
        if (computation.Liability.MarginalRelief > 0m)
            facts.Add(M("MarginalRelief", computation.Liability.MarginalRelief, taxDuration));
        var allocation = CorporationTaxFinancialYearAllocation.Create(computation);
        facts.Add(N("AssociatedCompaniesFY1",
            new XbrlValue.Integer(computation.Liability.AssociatedCompanyCount), taxDuration, 0));
        AddFinancialYear(allocation.Years[0], 1);
        if (allocation.Years.Count > 1)
        {
            facts.Add(N("AssociatedCompaniesFY2",
                new XbrlValue.Integer(computation.Liability.AssociatedCompanyCount), taxDuration, 0));
            AddFinancialYear(allocation.Years[1], 2);
        }
        return new("Corporation Tax computation", taxonomy, facts);

        void AddFinancialYear(CorporationTaxFinancialYearCharge charge, int number)
        {
            facts.Add(V($"FinancialYear{number}",
                new XbrlValue.Text(charge.FinancialYear.ToString(), null), taxDuration));
            foreach (var (band, index) in charge.Bands.Select((band, index) => (band, index)))
            {
                if (index > 1) throw new InvalidOperationException("The supported CT600 profile permits at most two ordinary Corporation Tax rates per financial year.");
                var ordinal = index == 0 ? "First" : "Second";
                facts.Add(M($"FY{number}Profit{ordinal}Rate", band.Profit, taxDuration));
                facts.Add(N($"FY{number}{ordinal}Rate", new XbrlValue.Decimal(band.Rate * 100m), taxDuration, 2));
                facts.Add(M($"FY{number}Tax{ordinal}Rate", band.Tax, taxDuration));
            }
        }
    }

    private static XbrlDimensionMember Dimension(string dimension, string member) =>
        new(Name(dimension), Name(member));

    private static XbrlQName Name(string localName) =>
        new(CorporationTaxComputationCatalog.Namespace, localName);
}
