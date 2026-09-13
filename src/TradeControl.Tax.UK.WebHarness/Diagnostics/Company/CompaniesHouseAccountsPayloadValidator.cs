namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CompaniesHouseAccountsPayloadValidator(CompanyAccountsPayloadValidator accountsValidator)
{
    public CompanyAccountsPayloadValidation Validate(CompaniesHouseAccountsPayload? payload)
    {
        var errors = accountsValidator.Validate(payload, "companies-house-accounts").Errors
            .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var values)) errors[field] = values = [];
            values.Add(message);
        }

        if (payload is not null)
        {
            if (string.IsNullOrWhiteSpace(payload.Filing.EnvelopeNumber))
                Add("filing.envelopeNumber", "A Companies House envelope number is required.");
            if (payload.Filing.ContractVersion is not ("TIS-5.9" or "future-api"))
                Add("filing.contractVersion", "Contract version must be 'TIS-5.9' or 'future-api'.");
            if (payload.Filing.ContractVersion == "future-api" && !payload.Filing.AllowPreviewContract)
                Add("filing.allowPreviewContract", "The future Companies House contract requires explicit preview opt-in.");
        }

        return new(errors.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }
}
