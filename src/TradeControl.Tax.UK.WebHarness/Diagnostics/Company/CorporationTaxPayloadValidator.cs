using Microsoft.Data.SqlClient;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CorporationTaxPayloadValidator
{
    public CompanyAccountsPayloadValidation Validate(CorporationTaxPayload? payload)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var values)) errors[field] = values = [];
            values.Add(message);
        }
        if (payload is null)
        {
            Add("payload", "A JSON payload is required.");
            return Result();
        }

        if (string.IsNullOrWhiteSpace(payload.SqlConnection)) Add("sqlConnection", "A SQL connection string is required.");
        else try { _ = new SqlConnectionStringBuilder(payload.SqlConnection); }
            catch (ArgumentException) { Add("sqlConnection", "The SQL connection string is invalid."); }
        if (!string.Equals(payload.Pilot, "corporation-tax", StringComparison.OrdinalIgnoreCase))
            Add("pilot", "Pilot must be 'corporation-tax'.");
        if (payload.AsOfDate is null) Add("asOfDate", "An as-of date is required.");
        if (payload.AccountsPeriod is not { } accounts || accounts.Start == default || accounts.End < accounts.Start)
            Add("accountsPeriod", "A valid inclusive accounts period is required.");
        if (string.IsNullOrWhiteSpace(payload.CorrelationId)) Add("correlationId", "A diagnostic correlation ID is required.");
        if (payload.Identity.CompanyNumber is { Length: > 0 } number && number.Length != 8)
            Add("identity.companyNumber", "The reviewed company number must contain eight characters.");
        if (string.IsNullOrWhiteSpace(payload.Identity.Utr)) Add("identity.utr", "A reviewed Corporation Tax UTR is required.");
        if (string.IsNullOrWhiteSpace(payload.Declaration.DeclarantName))
            Add("declaration.declarantName", "A reviewed declarant name is required.");
        if (payload.Declaration.DeclarationDate == default)
            Add("declaration.declarationDate", "A reviewed declaration date is required.");
        if (payload.Accounts.ApprovedOn == default) Add("accounts.approvedOn", "The accounts approval date is required.");
        if (string.IsNullOrWhiteSpace(payload.Accounts.SigningDirectorCode))
            Add("accounts.signingDirectorCode", "The signing-director code is required.");
        if (string.IsNullOrWhiteSpace(payload.Accounts.SigningDirectorName))
            Add("accounts.signingDirectorName", "The signing-director name is required.");
        if (payload.Accounts.AverageEmployees < 0) Add("accounts.averageEmployees", "Average employees cannot be negative.");
        foreach (var (advance, index) in payload.Accounts.DirectorAdvances.Select((value, index) => (value, index)))
            if (advance.OpeningBalance + advance.Advances - advance.Repayments != advance.ClosingBalance)
                Add($"accounts.directorAdvances[{index}].closingBalance", "The director advance does not reconcile.");
        if (payload.AccountsPeriod is { } approvalPeriod && payload.Accounts.ApprovedOn < approvalPeriod.End)
            Add("accounts.approvedOn", "The accounts approval date cannot precede the accounts period end.");
        if (payload.AccountsPeriod is { } declarationPeriod && payload.Declaration.DeclarationDate < declarationPeriod.End)
            Add("declaration.declarationDate", "The declaration date cannot precede the accounts period end.");

        if (payload.Periods.Count is < 1 or > 2) Add("periods", "One or two Corporation Tax periods are required.");
        if (payload.Periods.Count > 1 && payload.ReturnPeriodEnd is null)
            Add("returnPeriodEnd", "Select the Corporation Tax return period to preview for a long accounts period.");
        if (payload.ReturnPeriodEnd is { } selectedEnd && !payload.Periods.Any(value => value.Period.End == selectedEnd))
            Add("returnPeriodEnd", "The selected return-period end is not present in periods.");
        foreach (var (period, index) in payload.Periods.Select((value, index) => (value, index)))
        {
            var path = $"periods[{index}]";
            if (period.Period.Start == default || period.Period.End < period.Period.Start)
                Add(path + ".period", "A valid inclusive Corporation Tax period is required.");
            if (period.Period.End.DayNumber - period.Period.Start.DayNumber + 1 > 366)
                Add(path + ".period", "A Corporation Tax period cannot exceed twelve months.");
            if (period.OtherAddBacks.Any(value => value.Amount < 0m)) Add(path + ".otherAddBacks", "Add-backs cannot be negative.");
            if (period.Deductions.Any(value => value.Amount < 0m)) Add(path + ".deductions", "Deductions cannot be negative.");
            if (period.CapitalAllowances.WritingDownAllowance < 0m
                || period.CapitalAllowances.AnnualInvestmentAllowance < 0m
                || period.CapitalAllowances.OtherAllowances < 0m)
                Add(path + ".capitalAllowances", "Capital allowances cannot be negative.");
            if (period.ChargeableGains < 0m) Add(path + ".chargeableGains", "Chargeable gains cannot be negative.");
            if (period.OtherReliefs < 0m) Add(path + ".otherReliefs", "Reliefs cannot be negative.");
            if (period.LossesUsed < 0m) Add(path + ".lossesUsed", "Losses used cannot be negative.");
            var loans = period.LoansToParticipators;
            if (loans is not null && (loans.LoansOutstandingAtPeriodEnd < 0m || loans.TaxChargeable < 0m || loans.TaxPaid < 0m))
                Add(path + ".loansToParticipators", "Participator-loan values cannot be negative.");
            if (period.Extra is { Count: > 0 })
                foreach (var name in period.Extra.Keys) Add(path + "." + name, "Unknown period property.");
        }
        if (payload.AccountsPeriod is { } selected && payload.Periods.Count > 0)
        {
            var ordered = payload.Periods.OrderBy(value => value.Period.Start).ToArray();
            if (ordered[0].Period.Start != selected.Start || ordered[^1].Period.End != selected.End
                || ordered.Zip(ordered.Skip(1)).Any(pair => pair.First.Period.End.AddDays(1) != pair.Second.Period.Start))
                Add("periods", "Corporation Tax periods must cover the accounts period contiguously.");
        }
        if (payload.Extra is { Count: > 0 })
            foreach (var name in payload.Extra.Keys) Add(name, "Unknown payload property.");

        return Result();
        CompanyAccountsPayloadValidation Result() => new(errors.ToDictionary(
            item => item.Key, item => item.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }
}
