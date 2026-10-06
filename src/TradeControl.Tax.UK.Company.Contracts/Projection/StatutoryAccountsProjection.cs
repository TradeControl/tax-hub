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
        XbrlContext Dimensioned(XbrlQName dimension, XbrlQName member) => new(
            duration.EntityScheme, duration.EntityIdentifier, duration.Period,
            [new XbrlDimensionMember(dimension, member)]);
        var accountsStatus = Dimensioned(
            new(Frc2026Catalog.BusinessNamespace, "AccountsStatusDimension"),
            new(Frc2026Catalog.BusinessNamespace, "AuditExempt-NoAccountantsReport"));
        var accountsType = Dimensioned(
            new(Frc2026Catalog.BusinessNamespace, "AccountsTypeDimension"),
            new(Frc2026Catalog.BusinessNamespace, filleted ? "FilletedAccounts" : "FullAccounts"));
        var accountingStandards = Dimensioned(
            new(Frc2026Catalog.BusinessNamespace, "AccountingStandardsDimension"),
            new(Frc2026Catalog.BusinessNamespace, "Micro-entities"));
        var facts = new List<XbrlFact>
        {
            Text("CompanyName", duration, accounts.Company.CompanyName),
            Text("CompanyNumber", duration, accounts.Company.CompanyNumber),
            Date("BalanceSheetDate", instant, accounts.Period.End),
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
            // These date-valued facts describe the current accounting period. Their values are the
            // approval/start dates, while their XBRL context must identify the current period end.
            Date("AccountsApprovedOn", instant, accounts.Approval.ApprovedOn),
            Date("PeriodStart", instant, accounts.Period.Start),
            Date("PeriodEnd", instant, accounts.Period.End),
            Fixed("AccountsStatus", accountsStatus),
            Fixed("AccountsType", accountsType),
            Fixed("AccountingStandards", accountingStandards),
            Fixed("TradingStatus", duration),
            Boolean("DormantStatus", duration, false),
            Fixed("DirectorSigning", duration),
            Text("MicroEntityProvisions", duration, "These accounts have been prepared in accordance with the micro-entity provisions."),
            Text("Section477", duration, $"For the year ending {accounts.Period.End:dd MMMM yyyy} the company was entitled to exemption under section 477 of the Companies Act 2006 relating to small companies."),
            Text("MembersNoAudit", duration, "The members have not required the company to obtain an audit in accordance with section 476 of the Companies Act 2006."),
            Text("DirectorsResponsibilities", duration, "The directors acknowledge their responsibilities for complying with the requirements of the Act with respect to accounting records and the preparation of accounts.")
        };

        AddComparatives(accounts.BalanceSheet, comparativeInstant, facts, gbp);

        if (!filleted)
        {
            AddIncomeStatement(accounts.IncomeStatement, duration, facts, gbp, current: true);
            if (comparativeDuration is not null)
                AddIncomeStatement(accounts.IncomeStatement, comparativeDuration, facts, gbp, current: false);
        }

        return new(title, catalog, facts, new(IxbrlPresentationKind.StatutoryAccounts,
            accounts.Currency, filleted, accounts.Approval.SigningDirectorName));

        TaxonomyConcept C(string key) => catalog.Concepts[key];
        XbrlFact Text(string key, XbrlContext context, string value) => new(C(key).Name, context, new XbrlValue.Text(value));
        XbrlFact Fixed(string key, XbrlContext context) => new(C(key).Name, context, new XbrlValue.Fixed());
        XbrlFact Date(string key, XbrlContext context, DateOnly value) => new(C(key).Name, context, new XbrlValue.Date(value));
        XbrlFact Money(string key, XbrlContext context, decimal value, XbrlUnit unit) => new(C(key).Name, context, new XbrlValue.Monetary(value), unit, 0);
        XbrlFact Integer(string key, XbrlContext context, long value) => new(C(key).Name, context, new XbrlValue.Integer(value), XbrlUnit.Pure, 0);
        XbrlFact Boolean(string key, XbrlContext context, bool value) => new(C(key).Name, context, new XbrlValue.Boolean(value));

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
