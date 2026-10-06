namespace TradeControl.Tax.UK.Company.Xbrl;

public static class Frc2026Catalog
{
    public const string CoreNamespace = "http://xbrl.frc.org.uk/fr/2026-01-01/core";
    public const string BusinessNamespace = "http://xbrl.frc.org.uk/cd/2026-01-01/business";
    public const string DirectorsReportNamespace = "http://xbrl.frc.org.uk/reports/2026-01-01/direp";
    public const string Release = "FRC-2026-v1.0.0";

    private static TaxonomyConcept Concept(string key, string localName, string dataType, string periodType, bool monetary = true) =>
        new(key, new(CoreNamespace, localName), dataType, periodType, monetary);
    private static TaxonomyConcept Business(string key, string localName, string dataType = "fixed", string periodType = "duration") =>
        new(key, new(BusinessNamespace, localName), dataType, periodType, false);
    private static TaxonomyConcept DirectorsReport(string key, string localName) =>
        new(key, new(DirectorsReportNamespace, localName), "string", "duration", false);

    public static TaxonomyContract MicroEntity { get; } = new(
        Release,
        new Uri("https://xbrl.frc.org.uk/FRS-102/2026-01-01/FRS-102-2026-01-01.xsd"),
        new DateOnly(2025, 11, 18),
        "Production",
        new Dictionary<string, TaxonomyConcept>(StringComparer.Ordinal)
        {
            ["CompanyName"] = Business("CompanyName", "EntityCurrentLegalOrRegisteredName", "string"),
            ["CompanyNumber"] = Business("CompanyNumber", "UKCompaniesHouseRegisteredNumber", "string"),
            ["BalanceSheetDate"] = Business("BalanceSheetDate", "BalanceSheetDate", "date", "instant"),
            ["Turnover"] = Concept("Turnover", "TurnoverRevenue", "monetary", "duration"),
            ["OtherIncome"] = Concept("OtherIncome", "OtherOperatingIncome", "monetary", "duration"),
            ["CostOfSales"] = Concept("CostOfSales", "CostSales", "monetary", "duration"),
            ["AdministrativeExpenses"] = Concept("AdministrativeExpenses", "AdministrativeExpenses", "monetary", "duration"),
            ["TaxOnProfit"] = Concept("TaxOnProfit", "TaxTaxCreditOnProfitOrLossOnOrdinaryActivities", "monetary", "duration"),
            ["ProfitLoss"] = Concept("ProfitLoss", "ProfitLoss", "monetary", "duration"),
            ["FixedAssets"] = Concept("FixedAssets", "FixedAssets", "monetary", "instant"),
            ["CurrentAssets"] = Concept("CurrentAssets", "CurrentAssets", "monetary", "instant"),
            ["CreditorsWithinOneYear"] = Concept("CreditorsWithinOneYear", "CurrentLiabilities", "monetary", "instant"),
            ["Prepayments"] = Concept("Prepayments", "PrepaymentsAccruedIncome", "monetary", "instant"),
            ["NetCurrentAssets"] = Concept("NetCurrentAssets", "NetCurrentAssetsLiabilities", "monetary", "instant"),
            ["TotalAssetsLessCurrentLiabilities"] = Concept("TotalAssetsLessCurrentLiabilities", "TotalAssetsLessCurrentLiabilities", "monetary", "instant"),
            ["CreditorsAfterOneYear"] = Concept("CreditorsAfterOneYear", "Non-currentLiabilities", "monetary", "instant"),
            ["Provisions"] = Concept("Provisions", "ProvisionsForLiabilitiesBalanceSheetSubtotal", "monetary", "instant"),
            ["AccrualsDeferredIncome"] = Concept("AccrualsDeferredIncome", "AccruedLiabilitiesDeferredIncome", "monetary", "instant"),
            ["NetAssets"] = Concept("NetAssets", "NetAssetsLiabilities", "monetary", "instant"),
            ["CapitalAndReserves"] = Concept("CapitalAndReserves", "Equity", "monetary", "instant"),
            ["AverageEmployees"] = Concept("AverageEmployees", "AverageNumberEmployeesDuringPeriod", "integer", "duration", false),
            ["PrincipalActivity"] = Business("PrincipalActivity", "DescriptionPrincipalActivities", "string"),
            ["AccountsApprovedOn"] = Concept("AccountsApprovedOn", "DateAuthorisationFinancialStatementsForIssue", "date", "instant", false),
            ["PeriodStart"] = Business("PeriodStart", "StartDateForPeriodCoveredByReport", "date", "instant"),
            ["PeriodEnd"] = Business("PeriodEnd", "EndDateForPeriodCoveredByReport", "date", "instant"),
            ["AccountsStatus"] = Business("AccountsStatus", "AccountsStatusAuditedOrUnaudited"),
            ["AccountsType"] = Business("AccountsType", "AccountsType"),
            ["AccountingStandards"] = Business("AccountingStandards", "AccountingStandardsApplied"),
            ["TradingStatus"] = Business("TradingStatus", "EntityTradingStatus"),
            ["DormantStatus"] = Business("DormantStatus", "EntityDormantTruefalse", "boolean"),
            ["DirectorSigning"] = Concept("DirectorSigning", "DirectorSigningFinancialStatements", "fixed", "duration", false),
            ["MicroEntityProvisions"] = DirectorsReport("MicroEntityProvisions", "StatementThatAccountsHaveBeenPreparedInAccordanceWithProvisionsSmallCompaniesRegime"),
            ["Section477"] = DirectorsReport("Section477", "StatementThatCompanyEntitledToExemptionFromAuditUnderSection477CompaniesAct2006RelatingToSmallCompanies"),
            ["MembersNoAudit"] = DirectorsReport("MembersNoAudit", "StatementThatMembersHaveNotRequiredCompanyToObtainAnAudit"),
            ["DirectorsResponsibilities"] = DirectorsReport("DirectorsResponsibilities", "StatementThatDirectorsAcknowledgeTheirResponsibilitiesUnderCompaniesAct")
        });
}
