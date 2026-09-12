namespace TradeControl.Tax.UK.Application.DataProvision;

public enum SuggestedValueOrigin
{
    Source,
    Default,
    OperatorOverride
}

public sealed record SuggestedValue<T>(T Value, SuggestedValueOrigin Origin, string SourceCode)
{
    public SuggestedValue<T> Override(T value) =>
        new(value, SuggestedValueOrigin.OperatorOverride, "SubmissionOperator");
}

public sealed record SourceVersion(string SourceCode, string RowVersion, DateTime? UpdatedOn);

public sealed record StatutoryIdentityEvidence(
    string SubjectCode,
    string SubjectName,
    short BusinessTaxTypeCode,
    string JurisdictionCode,
    string RegistryJurisdictionCode,
    string CurrencyCode,
    string? CompanyNumber,
    string? VatNumber,
    string? BusinessDescription,
    int NumberOfEmployees,
    string? TradingAddress,
    string? RegisteredAddress,
    string? StatutoryAddress,
    IReadOnlyList<SourceVersion> Versions);

public sealed record RegistrationEvidence(
    string SchemeCode,
    string DisplayValue,
    bool IsSensitive,
    bool IsReviewed,
    string ValueSourceCode,
    SourceVersion Version);

public sealed record ReportingProfileEvidence(
    string ProfileCode,
    string ReportingTypeCode,
    string AuthorityCode,
    string? TaxSourceCode,
    string? AuthorityReferenceDisplay,
    bool IsReviewed,
    string ValueSourceCode,
    SourceVersion Version);

public sealed record ReportingSettingEvidence(
    string ProfileCode,
    string SettingCode,
    string ValueTypeCode,
    string DisplayValue,
    bool IsSensitive,
    bool IsReviewed,
    string ValueSourceCode,
    SourceVersion Version);

public sealed record ReportingWindow(DateOnly Start, DateOnly End);

public sealed record StatutoryContextSnapshot(
    StatutoryIdentityEvidence Identity,
    IReadOnlyList<RegistrationEvidence> Registrations,
    IReadOnlyList<ReportingProfileEvidence> Profiles,
    IReadOnlyList<ReportingSettingEvidence> Settings,
    ReportingWindow BusinessTaxWindow);

public interface IStatutoryContextSource
{
    Task<StatutoryContextSnapshot> ReadAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken = default);
}

public sealed record CompanyAccountsDraftDefaults(
    SuggestedValue<string> PrincipalActivity,
    SuggestedValue<string> AccountingPolicies,
    SuggestedValue<int> AverageEmployees,
    SuggestedValue<ReportingWindow> Period,
    SuggestedValue<ReportingWindow> ComparativePeriod,
    SuggestedValue<IReadOnlyList<DirectorAdvanceDraft>> DirectorAdvances,
    SuggestedValue<IReadOnlyList<CommitmentDraft>> CommitmentsAndContingencies)
{
    public static CompanyAccountsDraftDefaults Create(StatutoryContextSnapshot context)
    {
        if (context.Identity.BusinessTaxTypeCode != 0)
            throw new InvalidOperationException("Company accounts defaults require a company context.");

        var accountsProfile = context.Profiles.FirstOrDefault(profile =>
            profile.ReportingTypeCode == "STATUTORY-ACCOUNTS");
        var policies = accountsProfile is null ? null : context.Settings.FirstOrDefault(setting =>
            setting.ProfileCode == accountsProfile.ProfileCode
            && setting.SettingCode == "ACCOUNTING-POLICIES");

        return new(
            new(context.Identity.BusinessDescription ?? string.Empty,
                SuggestedValueOrigin.Source, "Subject.tbVirtual.BusinessDescription"),
            policies is null
                ? new("These accounts use the historical-cost basis and FRS 105.",
                    SuggestedValueOrigin.Default, "SubmissionDefault")
                : new(policies.DisplayValue,
                    SuggestedValueOrigin.Source, "Cash.tbReportingProfileSetting:ACCOUNTING-POLICIES"),
            new(context.Identity.NumberOfEmployees,
                SuggestedValueOrigin.Default, "Subject.tbVirtual.NumberOfEmployees"),
            new(context.BusinessTaxWindow,
                SuggestedValueOrigin.Default, "Cash.fnTaxTypeDueDates"),
            new(new ReportingWindow(
                    context.BusinessTaxWindow.Start.AddYears(-1),
                    context.BusinessTaxWindow.End.AddYears(-1)),
                SuggestedValueOrigin.Default, "PriorBusinessTaxWindow"),
            new(Array.Empty<DirectorAdvanceDraft>(), SuggestedValueOrigin.Default, "SubmissionDefault"),
            new(Array.Empty<CommitmentDraft>(), SuggestedValueOrigin.Default, "SubmissionDefault"));
    }
}

public sealed record DirectorAdvanceDraft(
    string DirectorName,
    decimal OpeningBalance,
    decimal Advances,
    decimal Repayments,
    decimal ClosingBalance,
    string Terms);

public sealed record CommitmentDraft(string Description, decimal? Amount);

public sealed record TaxAdjustmentDraft(string Description, decimal Amount);
public sealed record CapitalAllowanceDraft(
    decimal WritingDownAllowance,
    decimal AnnualInvestmentAllowance,
    decimal OtherAllowances);
public sealed record LossReliefDraft(
    decimal BroughtForward,
    decimal CurrentPeriod,
    decimal Used,
    decimal CarriedForward);

public sealed record CorporationTaxDraftDefaults(
    SuggestedValue<ReportingWindow> Period,
    SuggestedValue<IReadOnlyList<TaxAdjustmentDraft>> OtherAddBacks,
    SuggestedValue<IReadOnlyList<TaxAdjustmentDraft>> Deductions,
    SuggestedValue<CapitalAllowanceDraft> CapitalAllowances,
    SuggestedValue<LossReliefDraft> LossRelief,
    SuggestedValue<decimal> OtherReliefs)
{
    public static CorporationTaxDraftDefaults Create(StatutoryContextSnapshot context)
    {
        if (context.Identity.BusinessTaxTypeCode != 0)
            throw new InvalidOperationException("Corporation Tax defaults require a company context.");

        return new(
            new(context.BusinessTaxWindow, SuggestedValueOrigin.Default, "Cash.fnTaxTypeDueDates"),
            EmptyAdjustments(), EmptyAdjustments(),
            new(new(0m, 0m, 0m), SuggestedValueOrigin.Default, "SubmissionDefault"),
            new(new(0m, 0m, 0m, 0m), SuggestedValueOrigin.Default, "SubmissionDefault"),
            new(0m, SuggestedValueOrigin.Default, "SubmissionDefault"));

        static SuggestedValue<IReadOnlyList<TaxAdjustmentDraft>> EmptyAdjustments() =>
            new(Array.Empty<TaxAdjustmentDraft>(), SuggestedValueOrigin.Default, "SubmissionDefault");
    }
}

public sealed record DataProvisionFinding(string Code, string Message);

public static class StatutoryContextVerifier
{
    public static IReadOnlyList<DataProvisionFinding> Verify(StatutoryContextSnapshot context)
    {
        var findings = new List<DataProvisionFinding>();
        var identity = context.Identity;

        Required(identity.SubjectName, "LEGAL-NAME-MISSING", "The reporting name is missing.");
        Required(identity.RegistryJurisdictionCode, "REGISTRY-JURISDICTION-MISSING", "The registry jurisdiction is missing.");
        Required(identity.CurrencyCode, "CURRENCY-MISSING", "The reporting currency is missing.");
        Required(identity.StatutoryAddress, "STATUTORY-ADDRESS-MISSING", "The statutory address is missing.");

        if (identity.Versions.Count == 0 || identity.Versions.Any(version => string.IsNullOrWhiteSpace(version.RowVersion)))
            findings.Add(new("IDENTITY-PROVENANCE-MISSING", "Identity source provenance is incomplete."));

        foreach (var registration in context.Registrations.Where(item => item.IsSensitive))
        {
            if (!registration.DisplayValue.Contains('*'))
                findings.Add(new("SENSITIVE-IDENTIFIER-UNMASKED", $"{registration.SchemeCode} is not masked."));
        }

        foreach (var setting in context.Settings.Where(item => item.IsSensitive))
        {
            if (!setting.DisplayValue.Contains('*'))
                findings.Add(new("SENSITIVE-SETTING-UNMASKED", $"{setting.SettingCode} is not masked."));
        }

        if (context.Profiles.Any(profile => !profile.IsReviewed))
            findings.Add(new("PROFILE-UNREVIEWED", "A reporting profile has not been reviewed."));

        if (context.Settings.Any(setting => !setting.IsReviewed))
            findings.Add(new("SETTING-UNREVIEWED", "A reporting setting has not been reviewed."));

        return findings;

        void Required(string? value, string code, string message)
        {
            if (string.IsNullOrWhiteSpace(value))
                findings.Add(new(code, message));
        }
    }
}
