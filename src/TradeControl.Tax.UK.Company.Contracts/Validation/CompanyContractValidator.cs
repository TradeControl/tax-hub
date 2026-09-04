using TradeControl.Tax.UK.Company.Statutory;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.Hmrc.CorporationTax.Submission.V2026;

namespace TradeControl.Tax.UK.Company.Validation;

public enum ValidationSeverity { Warning, Error }

public sealed record ValidationFinding(ValidationSeverity Severity, string Code, string Message, string Path, string RuleSource);

public sealed record ContractValidationResult(IReadOnlyList<ValidationFinding> Findings)
{
    public bool IsValid => Findings.All(x => x.Severity != ValidationSeverity.Error);
}

public sealed class CompanyContractValidator
{
    public ContractValidationResult Validate(StatutoryAccounts accounts)
    {
        var f = new List<ValidationFinding>();
        ErrorIf(string.IsNullOrWhiteSpace(accounts.Company.CompanyName), "CO001", "Company name is required.", "Company.CompanyName");
        ErrorIf(accounts.Company.CompanyNumber.Length != 8, "CO002", "Company number must contain eight characters.", "Company.CompanyNumber");
        ErrorIf(accounts.Period.End < accounts.Period.Start, "PER001", "Accounts period ends before it starts.", "Period");
        ErrorIf(accounts.Profile.Framework != ReportingFramework.Frs105 || accounts.Profile.Type != AccountsType.MicroEntity,
            "PROFILE001", "Only FRS 105 micro-entity accounts are supported.", "Profile");
        ErrorIf(accounts.Profile.AuditStatus != AuditStatus.UnauditedExempt,
            "PROFILE002", "Audited accounts are outside the initial supported profile.", "Profile.AuditStatus");
        ErrorIf(accounts.Notes.AverageEmployees < 0, "ACC001", "Average employees cannot be negative.", "Notes.AverageEmployees");
        ErrorIf(accounts.BalanceSheet.NetAssetsLiabilities.Current != accounts.BalanceSheet.CapitalAndReserves.Current,
            "ACC002", "Net assets must equal capital and reserves.", "BalanceSheet.CapitalAndReserves");
        ErrorIf(!accounts.Profile.MembersHaveNotRequiredAudit || !accounts.Profile.DirectorsAcknowledgeResponsibilities,
            "ACC003", "The supported unaudited micro profile requires both statutory statements.", "Profile");
        ErrorIf(accounts.Approval.ApprovedOn < accounts.Period.End, "ACC004", "Approval cannot precede the accounts period end.", "Approval.ApprovedOn");
        ErrorIf(accounts.BalanceSheet.CurrentAssets.Current + accounts.BalanceSheet.PrepaymentsAndAccruedIncome.Current - accounts.BalanceSheet.CreditorsDueWithinOneYear.Current != accounts.BalanceSheet.NetCurrentAssetsLiabilities.Current,
            "ACC005", "Net current assets do not reconcile.", "BalanceSheet.NetCurrentAssetsLiabilities");
        ErrorIf(accounts.BalanceSheet.FixedAssets.Current + accounts.BalanceSheet.NetCurrentAssetsLiabilities.Current != accounts.BalanceSheet.TotalAssetsLessCurrentLiabilities.Current,
            "ACC006", "Total assets less current liabilities do not reconcile.", "BalanceSheet.TotalAssetsLessCurrentLiabilities");
        ErrorIf(accounts.BalanceSheet.TotalAssetsLessCurrentLiabilities.Current - accounts.BalanceSheet.CreditorsDueAfterOneYear.Current - accounts.BalanceSheet.Provisions.Current - accounts.BalanceSheet.AccrualsAndDeferredIncome.Current != accounts.BalanceSheet.NetAssetsLiabilities.Current,
            "ACC007", "Net assets do not reconcile to the balance-sheet components.", "BalanceSheet.NetAssetsLiabilities");
        foreach (var (advance, index) in accounts.Notes.DirectorAdvances.Select((value, index) => (value, index)))
            ErrorIf(advance.OpeningBalance + advance.Advances - advance.Repayments != advance.ClosingBalance,
                "NOTE001", "Director advance movement does not reconcile.", $"Notes.DirectorAdvances[{index}].ClosingBalance");
        return new(f);
        void ErrorIf(bool condition, string code, string message, string path) { if (condition) f.Add(new(ValidationSeverity.Error, code, message, path, "SupportedMicroCompanyProfile")); }
    }

    public ContractValidationResult Validate(CorporationTaxReturnPackage package)
    {
        var f = new List<ValidationFinding>();
        Add(package.Return.Period != package.Computation.CorporationTaxPeriod, "CT001", "CT600 and computation periods differ.", "Return.Period");
        Add(package.Return.ProfitBeforeTax != package.Computation.AccountsProfitLossBeforeTax, "CT002", "CT600 profit does not reconcile to computation.", "Return.ProfitBeforeTax");
        Add(package.Return.TaxableTotalProfits != package.Computation.TaxableTotalProfits, "CT003", "Taxable total profits do not reconcile.", "Return.TaxableTotalProfits");
        Add(package.Return.CorporationTaxChargeable != package.Computation.CorporationTaxChargeable, "CT004", "Corporation Tax charge does not reconcile.", "Return.CorporationTaxChargeable");
        Add(package.Return.TaxPayable != package.Computation.TaxPayable, "CT005", "Tax payable does not reconcile.", "Return.TaxPayable");
        Add(!package.Return.AccountsAttached || package.AccountsDocument.Content.Length == 0, "CT006", "A non-empty accounts iXBRL attachment is required.", "AccountsDocument");
        Add(!package.Return.ComputationsAttached || package.ComputationDocument.Content.Length == 0, "CT007", "A non-empty computation iXBRL attachment is required.", "ComputationDocument");
        if (package.Return.SupplementaryPageA is { } a)
            Add(a.LoansOutstandingAtPeriodEnd > 0 && a.TaxChargeable <= 0, "CT600A001", "Loans outstanding require a positive section 455 charge in the supported profile.", "Return.SupplementaryPageA.TaxChargeable");
        return new(f);
        void Add(bool condition, string code, string message, string path) { if (condition) f.Add(new(ValidationSeverity.Error, code, message, path, "CT600-RIM-1.994-supported-profile")); }
    }

    public ContractValidationResult Validate(IxbrlReport report)
    {
        var f = new List<ValidationFinding>();
        foreach (var fact in report.Facts)
        {
            if (fact.Value is XbrlValue.Monetary && fact.Unit is null)
                f.Add(new(ValidationSeverity.Error, "XBRL001", "A monetary fact requires a unit.", fact.Concept.ToString(), report.Taxonomy.ReleaseId));
            if (!report.Taxonomy.Concepts.Values.Any(x => x.Name == fact.Concept))
                f.Add(new(ValidationSeverity.Error, "XBRL002", "Fact concept is not in the selected supported catalog.", fact.Concept.ToString(), report.Taxonomy.ReleaseId));
        }
        var duplicates = report.Facts.GroupBy(x => (x.Concept, x.Context, x.Unit)).Where(x => x.Count() > 1);
        foreach (var duplicate in duplicates)
            f.Add(new(ValidationSeverity.Error, "XBRL003", "Duplicate fact aspects.", duplicate.Key.Concept.ToString(), report.Taxonomy.ReleaseId));
        return new(f);
    }
}
