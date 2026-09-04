namespace TradeControl.Tax.UK.Company.Xbrl;

public static class Frc2026Catalog
{
    public const string CoreNamespace = "http://xbrl.frc.org.uk/fr/2026-01-01/core";
    public const string Release = "FRC-2026-v1.0.0";

    private static TaxonomyConcept Concept(string key, string localName, string dataType, string periodType, bool monetary = true) =>
        new(key, new(CoreNamespace, localName), dataType, periodType, monetary);

    public static TaxonomyContract MicroEntity { get; } = new(
        Release,
        new Uri("http://xbrl.frc.org.uk/FRS-102/2026-01-01/FRS-102-2026-01-01.xsd"),
        new DateOnly(2025, 11, 18),
        "Production",
        new Dictionary<string, TaxonomyConcept>(StringComparer.Ordinal)
        {
            ["CompanyName"] = Concept("CompanyName", "EntityCurrentLegalOrRegisteredName", "string", "duration", false),
            ["CompanyNumber"] = Concept("CompanyNumber", "UKCompaniesHouseRegisteredNumber", "string", "duration", false),
            ["BalanceSheetDate"] = Concept("BalanceSheetDate", "BalanceSheetDate", "date", "duration", false),
            ["Turnover"] = Concept("Turnover", "TurnoverRevenue", "monetary", "duration"),
            ["OtherIncome"] = Concept("OtherIncome", "OtherOperatingIncome", "monetary", "duration"),
            ["CostOfSales"] = Concept("CostOfSales", "CostSales", "monetary", "duration"),
            ["AdministrativeExpenses"] = Concept("AdministrativeExpenses", "AdministrativeExpenses", "monetary", "duration"),
            ["TaxOnProfit"] = Concept("TaxOnProfit", "TaxOnProfitOrLossOnOrdinaryActivities", "monetary", "duration"),
            ["ProfitLoss"] = Concept("ProfitLoss", "ProfitLossForPeriod", "monetary", "duration"),
            ["FixedAssets"] = Concept("FixedAssets", "FixedAssets", "monetary", "instant"),
            ["CurrentAssets"] = Concept("CurrentAssets", "CurrentAssets", "monetary", "instant"),
            ["CreditorsWithinOneYear"] = Concept("CreditorsWithinOneYear", "CreditorsAmountsFallingDueWithinOneYear", "monetary", "instant"),
            ["Prepayments"] = Concept("Prepayments", "PrepaymentsAccruedIncome", "monetary", "instant"),
            ["NetCurrentAssets"] = Concept("NetCurrentAssets", "NetCurrentAssetsLiabilities", "monetary", "instant"),
            ["TotalAssetsLessCurrentLiabilities"] = Concept("TotalAssetsLessCurrentLiabilities", "TotalAssetsLessCurrentLiabilities", "monetary", "instant"),
            ["CreditorsAfterOneYear"] = Concept("CreditorsAfterOneYear", "CreditorsAmountsFallingDueAfterOneYear", "monetary", "instant"),
            ["Provisions"] = Concept("Provisions", "ProvisionsForLiabilitiesBalanceSheetSubtotal", "monetary", "instant"),
            ["AccrualsDeferredIncome"] = Concept("AccrualsDeferredIncome", "AccrualsDeferredIncome", "monetary", "instant"),
            ["NetAssets"] = Concept("NetAssets", "NetAssetsLiabilities", "monetary", "instant"),
            ["CapitalAndReserves"] = Concept("CapitalAndReserves", "CapitalAndReserves", "monetary", "instant"),
            ["AverageEmployees"] = Concept("AverageEmployees", "AverageNumberEmployeesDuringPeriod", "integer", "duration", false),
            ["PrincipalActivity"] = Concept("PrincipalActivity", "DescriptionPrincipalActivities", "string", "duration", false),
            ["AccountsApprovedOn"] = Concept("AccountsApprovedOn", "DateAuthorisationFinancialStatementsForIssue", "date", "duration", false)
        });
}
