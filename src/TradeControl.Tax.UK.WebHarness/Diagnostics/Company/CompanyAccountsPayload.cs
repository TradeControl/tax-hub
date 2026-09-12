using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CompanyAccountsPayload
{
    public string SqlConnection { get; set; } = string.Empty;
    public string Pilot { get; set; } = string.Empty;
    public string Profile { get; set; } = "full";
    public DateOnly? AsOfDate { get; set; }
    public ReportingWindow? Period { get; set; }
    public ReportingWindow? ComparativePeriod { get; set; }
    public bool IsFirstAccountsPeriod { get; set; }
    public CompanyAccountsReviewPayload Review { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CompanyAccountsReviewPayload
{
    public string? CompanyNumber { get; set; }
    public bool MembersHaveNotRequiredAudit { get; set; } = true;
    public bool DirectorsAcknowledgeResponsibilities { get; set; } = true;
    public decimal TaxOnProfit { get; set; }
    public decimal? ComparativeTaxOnProfit { get; set; }
    public decimal PrepaymentsAndAccruedIncome { get; set; }
    public decimal? ComparativePrepaymentsAndAccruedIncome { get; set; }
    public decimal Provisions { get; set; }
    public decimal? ComparativeProvisions { get; set; }
    public decimal AccrualsAndDeferredIncome { get; set; }
    public decimal? ComparativeAccrualsAndDeferredIncome { get; set; }
    public string? PrincipalActivity { get; set; }
    public string? AccountingPolicies { get; set; }
    public int? AverageEmployees { get; set; }
    public IReadOnlyList<DirectorAdvanceDraft> DirectorAdvances { get; set; } = [];
    public IReadOnlyList<CommitmentDraft> CommitmentsAndContingencies { get; set; } = [];
    public DateOnly ApprovedOn { get; set; }
    public string SigningDirectorCode { get; set; } = string.Empty;
    public string SigningDirectorName { get; set; } = string.Empty;
}

public sealed record CompanyAccountsPayloadValidation(
    IReadOnlyDictionary<string, string[]> Errors)
{
    public bool IsValid => Errors.Count == 0;
}
