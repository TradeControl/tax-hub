using Microsoft.Data.SqlClient;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CompanyAccountsPayloadValidator
{
    public CompanyAccountsPayloadValidation Validate(CompanyAccountsPayload? payload)
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

        if (string.IsNullOrWhiteSpace(payload.SqlConnection))
            Add("sqlConnection", "A SQL connection string is required.");
        else
        {
            try { _ = new SqlConnectionStringBuilder(payload.SqlConnection); }
            catch (ArgumentException) { Add("sqlConnection", "The SQL connection string is invalid."); }
        }
        if (!string.Equals(payload.Pilot, "company-accounts", StringComparison.OrdinalIgnoreCase))
            Add("pilot", "Pilot must be 'company-accounts'.");
        if (payload.Profile is not ("full" or "filleted"))
            Add("profile", "Profile must be 'full' or 'filleted'.");
        if (payload.Extra is { Count: > 0 })
            foreach (var name in payload.Extra.Keys) Add(name, "Unknown payload property.");
        if (payload.Period is { } period && period.Start > period.End)
            Add("period", "The reporting period start must not follow its end.");
        if (payload.ComparativePeriod is { } comparative && comparative.Start > comparative.End)
            Add("comparativePeriod", "The comparative period start must not follow its end.");
        if (payload.IsFirstAccountsPeriod && payload.ComparativePeriod is not null)
            Add("comparativePeriod", "A first accounts period cannot include a comparative period.");
        if (payload.Review.ApprovedOn == default)
            Add("review.approvedOn", "The reviewed approval date is required.");
        if (payload.Review.CompanyNumber is { Length: > 0 } companyNumber && companyNumber.Length != 8)
            Add("review.companyNumber", "The reviewed company number must contain eight characters.");
        if (string.IsNullOrWhiteSpace(payload.Review.SigningDirectorCode))
            Add("review.signingDirectorCode", "The reviewed signing-director code is required.");
        if (string.IsNullOrWhiteSpace(payload.Review.SigningDirectorName))
            Add("review.signingDirectorName", "The reviewed signing-director name is required.");
        if (payload.Review.AverageEmployees < 0)
            Add("review.averageEmployees", "Average employees cannot be negative.");
        foreach (var (advance, index) in payload.Review.DirectorAdvances.Select((value, index) => (value, index)))
            if (advance.OpeningBalance + advance.Advances - advance.Repayments != advance.ClosingBalance)
                Add($"review.directorAdvances[{index}].closingBalance", "The director advance does not reconcile.");

        return Result();

        CompanyAccountsPayloadValidation Result() => new(errors.ToDictionary(
            item => item.Key, item => item.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }
}
