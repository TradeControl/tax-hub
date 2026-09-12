using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed class CorporationTaxPayload
{
    public string SqlConnection { get; set; } = string.Empty;
    public string Pilot { get; set; } = string.Empty;
    public DateOnly? AsOfDate { get; set; }
    public ReportingWindow? AccountsPeriod { get; set; }
    public DateOnly? ReturnPeriodEnd { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public CorporationTaxIdentityPayload Identity { get; set; } = new();
    public CorporationTaxAccountsPayload Accounts { get; set; } = new();
    public IReadOnlyList<CorporationTaxPeriodPayload> Periods { get; set; } = [];
    public CorporationTaxDeclarationPayload Declaration { get; set; } = new();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CorporationTaxIdentityPayload
{
    public string? CompanyNumber { get; set; }
    public string Utr { get; set; } = string.Empty;
}

public sealed class CorporationTaxAccountsPayload
{
    public ReportingWindow? ComparativePeriod { get; set; }
    public bool IsFirstAccountsPeriod { get; set; }
    public bool MembersHaveNotRequiredAudit { get; set; } = true;
    public bool DirectorsAcknowledgeResponsibilities { get; set; } = true;
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

public sealed class CorporationTaxPeriodPayload
{
    public ReportingWindow Period { get; set; } = new(default, default);
    public IReadOnlyList<TaxAdjustmentDraft> OtherAddBacks { get; set; } = [];
    public IReadOnlyList<TaxAdjustmentDraft> Deductions { get; set; } = [];
    public CapitalAllowanceDraft CapitalAllowances { get; set; } = new(0m, 0m, 0m);
    public decimal LossesUsed { get; set; }
    public decimal ChargeableGains { get; set; }
    public decimal OtherReliefs { get; set; }
    public LoansToParticipatorsDraft? LoansToParticipators { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class CorporationTaxDeclarationPayload
{
    public string DeclarantName { get; set; } = string.Empty;
    public DateOnly DeclarationDate { get; set; }
}
