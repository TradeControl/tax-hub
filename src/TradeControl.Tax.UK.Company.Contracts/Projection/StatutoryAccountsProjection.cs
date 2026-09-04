using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Xbrl;

namespace TradeControl.Tax.UK.Company.Projection;

public sealed class StatutoryAccountsProjection
{
    public IxbrlReport Project(StatutoryAccounts accounts, bool filleted, string title)
    {
        var catalog = Frc2026Catalog.MicroEntity;
        var duration = new XbrlContext("http://www.companieshouse.gov.uk/", accounts.Company.CompanyNumber,
            new XbrlPeriod.Duration(accounts.Period.Start, accounts.Period.End));
        var instant = new XbrlContext("http://www.companieshouse.gov.uk/", accounts.Company.CompanyNumber,
            new XbrlPeriod.Instant(accounts.Period.End));
        var comparativeDuration = accounts.ComparativePeriod is null ? null : new XbrlContext(
            "http://www.companieshouse.gov.uk/", accounts.Company.CompanyNumber,
            new XbrlPeriod.Duration(accounts.ComparativePeriod.Start, accounts.ComparativePeriod.End));
        var comparativeInstant = accounts.ComparativePeriod is null ? null : new XbrlContext(
            "http://www.companieshouse.gov.uk/", accounts.Company.CompanyNumber,
            new XbrlPeriod.Instant(accounts.ComparativePeriod.End));
        var gbp = XbrlUnit.Currency(accounts.Currency);
        var facts = new List<XbrlFact>
        {
            Text("CompanyName", duration, accounts.Company.CompanyName),
            Text("CompanyNumber", duration, accounts.Company.CompanyNumber),
            Date("BalanceSheetDate", duration, accounts.Period.End),
            Money("FixedAssets", instant, accounts.BalanceSheet.FixedAssets.Current, gbp),
            Money("CurrentAssets", instant, accounts.BalanceSheet.CurrentAssets.Current, gbp),
            Money("Prepayments", instant, accounts.BalanceSheet.PrepaymentsAndAccruedIncome.Current, gbp),
            Money("CreditorsWithinOneYear", instant, accounts.BalanceSheet.CreditorsDueWithinOneYear.Current, gbp),
            Money("NetCurrentAssets", instant, accounts.BalanceSheet.NetCurrentAssetsLiabilities.Current, gbp),
            Money("TotalAssetsLessCurrentLiabilities", instant, accounts.BalanceSheet.TotalAssetsLessCurrentLiabilities.Current, gbp),
            Money("CreditorsAfterOneYear", instant, accounts.BalanceSheet.CreditorsDueAfterOneYear.Current, gbp),
            Money("Provisions", instant, accounts.BalanceSheet.Provisions.Current, gbp),
            Money("AccrualsDeferredIncome", instant, accounts.BalanceSheet.AccrualsAndDeferredIncome.Current, gbp),
            Money("NetAssets", instant, accounts.BalanceSheet.NetAssetsLiabilities.Current, gbp),
            Money("CapitalAndReserves", instant, accounts.BalanceSheet.CapitalAndReserves.Current, gbp),
            Integer("AverageEmployees", duration, accounts.Notes.AverageEmployees),
            Text("PrincipalActivity", duration, accounts.Notes.PrincipalActivity),
            Date("AccountsApprovedOn", duration, accounts.Approval.ApprovedOn)
        };

        AddComparatives(accounts.BalanceSheet, comparativeInstant, facts, gbp);

        if (!filleted)
        {
            AddIncomeStatement(accounts.IncomeStatement, duration, facts, gbp, current: true);
            if (comparativeDuration is not null)
                AddIncomeStatement(accounts.IncomeStatement, comparativeDuration, facts, gbp, current: false);
        }

        return new(title, catalog, facts);

        TaxonomyConcept C(string key) => catalog.Concepts[key];
        XbrlFact Text(string key, XbrlContext context, string value) => new(C(key).Name, context, new XbrlValue.Text(value));
        XbrlFact Date(string key, XbrlContext context, DateOnly value) => new(C(key).Name, context, new XbrlValue.Date(value));
        XbrlFact Money(string key, XbrlContext context, decimal value, XbrlUnit unit) => new(C(key).Name, context, new XbrlValue.Monetary(value), unit, 0);
        XbrlFact Integer(string key, XbrlContext context, long value) => new(C(key).Name, context, new XbrlValue.Integer(value), XbrlUnit.Pure, 0);

        void AddComparatives(StatementOfFinancialPosition statement, XbrlContext? context, List<XbrlFact> target, XbrlUnit unit)
        {
            if (context is null) return;
            Add("FixedAssets", statement.FixedAssets); Add("CurrentAssets", statement.CurrentAssets);
            Add("Prepayments", statement.PrepaymentsAndAccruedIncome); Add("CreditorsWithinOneYear", statement.CreditorsDueWithinOneYear);
            Add("NetCurrentAssets", statement.NetCurrentAssetsLiabilities); Add("TotalAssetsLessCurrentLiabilities", statement.TotalAssetsLessCurrentLiabilities);
            Add("CreditorsAfterOneYear", statement.CreditorsDueAfterOneYear); Add("Provisions", statement.Provisions);
            Add("AccrualsDeferredIncome", statement.AccrualsAndDeferredIncome); Add("NetAssets", statement.NetAssetsLiabilities);
            Add("CapitalAndReserves", statement.CapitalAndReserves);
            void Add(string key, ComparativeAmount amount) { if (amount.Comparative is { } value) target.Add(Money(key, context, value, unit)); }
        }

        void AddIncomeStatement(IncomeStatement statement, XbrlContext context, List<XbrlFact> target, XbrlUnit unit, bool current)
        {
            Add("Turnover", statement.Turnover); Add("OtherIncome", statement.OtherIncome); Add("CostOfSales", statement.CostOfSales);
            Add("AdministrativeExpenses", statement.AdministrativeExpenses); Add("TaxOnProfit", statement.TaxOnProfit);
            Add("ProfitLoss", statement.ProfitLossForPeriod);
            void Add(string key, ComparativeAmount amount)
            {
                var value = current ? amount.Current : amount.Comparative;
                if (value is not null) target.Add(Money(key, context, value.Value, unit));
            }
        }
    }
}
