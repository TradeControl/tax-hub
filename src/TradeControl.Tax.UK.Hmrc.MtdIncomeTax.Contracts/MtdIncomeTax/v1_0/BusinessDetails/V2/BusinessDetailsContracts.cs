using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessDetails.V2.Wire.list;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessDetails.V2.Wire.detail;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessDetails.V2.Wire.ladr;

namespace TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.BusinessDetails.V2;

public static class BusinessDetailsEndpoints
{
    private const string Version = "2.0";
    private const string Accept = "application/vnd.hmrc.2.0+json";
    private const string Root = "/individuals/business/details/{nino}/{businessId}";
    private static readonly EndpointParameter[] Nino = [new("nino")];
    private static readonly EndpointParameter[] Business = [new("nino"), new("businessId")];
    private static readonly EndpointParameter[] TaxYear = [new("nino"), new("businessId"), new("taxYear")];

    public static readonly HmrcEndpoint List = new("List businesses", "GET", "/individuals/business/details/{nino}/list", Version, Accept, "read:self-assessment", 200, Nino, [], false, ResponseType: typeof(BusinessListWireResponse));
    public static readonly HmrcEndpoint Retrieve = new("Retrieve business details", "GET", Root, Version, Accept, "read:self-assessment", 200, Business, [], false, ResponseType: typeof(BusinessDetailWireResponse));
    public static readonly HmrcEndpoint PutQuarterlyType = new("Create or amend quarterly period type", "PUT", Root + "/{taxYear}", Version, Accept, "write:self-assessment", 204, TaxYear, [], true, typeof(QuarterlyPeriodTypeRequest), ContentType: "application/json");
    public static readonly HmrcEndpoint GetAccountingType = new("Retrieve accounting type", "GET", Root + "/{taxYear}/accounting-type", Version, Accept, "read:self-assessment", 200, TaxYear, [], false, ResponseType: typeof(AccountingTypeResponse));
    public static readonly HmrcEndpoint PutAccountingType = new("Create or amend accounting type", "PUT", Root + "/{taxYear}/accounting-type", Version, Accept, "write:self-assessment", 204, TaxYear, [], true, typeof(AccountingTypeRequest), ContentType: "application/json");
    public static readonly HmrcEndpoint GetPeriodsOfAccount = new("Retrieve periods of account", "GET", Root + "/{taxYear}/periods-of-account", Version, Accept, "read:self-assessment", 200, TaxYear, [], false, ResponseType: typeof(PeriodsOfAccountResponse));
    public static readonly HmrcEndpoint PutPeriodsOfAccount = new("Create or amend periods of account", "PUT", Root + "/{taxYear}/periods-of-account", Version, Accept, "write:self-assessment", 204, TaxYear, [], true, typeof(PeriodsOfAccountRequest), ContentType: "application/json");
    public static readonly HmrcEndpoint GetLadrElection = new("Retrieve late accounting date rule election", "GET", Root + "/{taxYear}/late-accounting-date-rule-election", Version, Accept, "read:self-assessment", 200, TaxYear, [], false, ResponseType: typeof(LadrElectionWireResponse));
    public static readonly HmrcEndpoint CreateLadrElection = new("Disapply late accounting date rule", "POST", Root + "/{taxYear}/late-accounting-date-rule-election/disapply", Version, Accept, "write:self-assessment", 204, TaxYear, [], false);
    public static readonly HmrcEndpoint DeleteLadrElection = new("Withdraw late accounting date rule election", "DELETE", Root + "/{taxYear}/late-accounting-date-rule-election/withdraw", Version, Accept, "write:self-assessment", 204, TaxYear, [], false);

    public static IReadOnlyList<HmrcEndpoint> All => [List, Retrieve, PutQuarterlyType, GetAccountingType, PutAccountingType, GetPeriodsOfAccount, PutPeriodsOfAccount, GetLadrElection, CreateLadrElection, DeleteLadrElection];
}

public sealed class AccountingPeriod
{
    [JsonPropertyName("startDate")] public required DateOnly StartDate { get; set; }
    [JsonPropertyName("endDate")] public required DateOnly EndDate { get; set; }
}

public sealed class QuarterlyPeriodTypeRequest
{
    [JsonPropertyName("quarterlyPeriodType")]
    public required string QuarterlyPeriodType { get; set; }
}

public sealed class AccountingTypeRequest
{
    [JsonPropertyName("accountingType")]
    public required string AccountingType { get; set; }
}

public sealed class AccountingTypeResponse : HmrcResponse
{
    [JsonPropertyName("accountingType")]
    public required string AccountingType { get; set; }
}

public class PeriodsOfAccountRequest
{
    [JsonPropertyName("periodsOfAccount")]
    public required bool PeriodsOfAccount { get; set; }
    [JsonPropertyName("periodsOfAccountDates")]
    public List<AccountingPeriod>? PeriodsOfAccountDates { get; set; }
}

public sealed class PeriodsOfAccountResponse : PeriodsOfAccountRequest
{
    [JsonPropertyName("submittedOn")]
    public DateTimeOffset? SubmittedOn { get; set; }
}
