namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CompaniesHouseAccountsPayloadValidator(CompanyAccountsPayloadValidator accountsValidator)
{
    public CompanyAccountsPayloadValidation Validate(CompaniesHouseAccountsPayload? payload)
    {
        var commonPayload = payload is null ? null : new CompanyAccountsPayload
        {
            SqlConnection = payload.SqlConnection,
            Pilot = payload.Pilot,
            Profile = payload.Profile,
            AsOfDate = payload.AsOfDate,
            Period = payload.Period,
            ComparativePeriod = payload.ComparativePeriod,
            IsFirstAccountsPeriod = payload.IsFirstAccountsPeriod,
            Extra = payload.Extra,
            Review = new CompanyAccountsReviewPayload
            {
                CompanyNumber = payload.Review.CompanyNumber,
                MembersHaveNotRequiredAudit = payload.Review.MembersHaveNotRequiredAudit,
                DirectorsAcknowledgeResponsibilities = payload.Review.DirectorsAcknowledgeResponsibilities,
                PrepaymentsAndAccruedIncome = payload.Review.PrepaymentsAndAccruedIncome,
                ComparativePrepaymentsAndAccruedIncome = payload.Review.ComparativePrepaymentsAndAccruedIncome,
                Provisions = payload.Review.Provisions,
                ComparativeProvisions = payload.Review.ComparativeProvisions,
                AccrualsAndDeferredIncome = payload.Review.AccrualsAndDeferredIncome,
                ComparativeAccrualsAndDeferredIncome = payload.Review.ComparativeAccrualsAndDeferredIncome,
                PrincipalActivity = payload.Review.PrincipalActivity,
                AccountingPolicies = payload.Review.AccountingPolicies,
                AverageEmployees = payload.Review.AverageEmployees,
                DirectorAdvances = payload.Review.DirectorAdvances,
                CommitmentsAndContingencies = payload.Review.CommitmentsAndContingencies,
                ApprovedOn = payload.Review.ApprovedOn,
                SigningDirectorCode = payload.Review.SigningDirectorCode,
                SigningDirectorName = payload.Review.SigningDirectorName
            }
        };
        var errors = accountsValidator.Validate(commonPayload, "companies-house-accounts").Errors
            .ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
        void Add(string field, string message)
        {
            if (!errors.TryGetValue(field, out var values)) errors[field] = values = [];
            values.Add(message);
        }

        if (payload is not null)
        {
            if (payload.Profile != "filleted")
                Add("profile", "The Companies House pilot currently supports the filleted micro-entity profile.");
            if (payload.Review.Extra is { Count: > 0 })
                foreach (var name in payload.Review.Extra.Keys) Add($"review.{name}", "Unknown Companies House review property.");
            if (payload.Filing.SubmissionNumber.Length != 6)
                Add("filing.submissionNumber", "A six-character Companies House SubmissionNumber is required.");
            if (payload.Filing.ContractVersion is not ("TIS-6.0" or "future-api"))
                Add("filing.contractVersion", "Contract version must be 'TIS-6.0' or 'future-api'.");
            if (payload.Filing.ContractVersion == "future-api" && !payload.Filing.AllowPreviewContract)
                Add("filing.allowPreviewContract", "The future Companies House contract requires explicit preview opt-in.");
            if (payload.Simulation is null)
                Add("simulation", "A Companies House simulator selection is required.");
            else if (payload.Simulation.Scenario is not ("happy-accept" or "business-reject"
                or "parked-then-accept" or "internal-failure" or "general-poll-redelivery"))
                Add("simulation.scenario", "The Companies House simulator scenario is unsupported.");
        }

        return new(errors.ToDictionary(x => x.Key, x => x.Value.ToArray(), StringComparer.OrdinalIgnoreCase));
    }
}
