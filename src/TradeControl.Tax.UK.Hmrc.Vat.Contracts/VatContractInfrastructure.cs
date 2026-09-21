using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.CustomerInformation;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.FinancialDetails;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Liabilities;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Obligations;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Payments;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Penalties;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.Returns;
using TradeControl.Tax.UK.Hmrc.Vat.v1_0.ViewReturn;

namespace TradeControl.Tax.UK.Hmrc.Vat;

public enum VatRequestShape { AccountingBody, BodylessEnquiry }
public enum VatAccountsModeDecision { Supported, Deferred, Unsupported }

public sealed record VatOperationDescriptor(string OperationId, string Method, string PathTemplate,
    string ApiVersion, string OAuthScope, IReadOnlyList<string> PathParameters,
    IReadOnlyList<string> QueryParameters, VatRequestShape Shape, Type? RequestType, Type? ResponseType,
    string Accept, string? ContentType, VatAccountsModeDecision AccountsMode, string RequiredSource,
    string PlannedUseCase, string WebHarnessRoute, bool HasContractFixture,
    bool HasPopulationFixture, bool HasHarnessCoverage);

public static class VatOperationCatalog
{
    private const string Accept = "application/vnd.hmrc.1.0+json";
    private static readonly string[] Vrn = ["vrn"];

    public static IReadOnlyList<VatOperationDescriptor> All { get; } =
    [
        Enquiry("vat.customer-information.retrieve", VatCustomerInformationEndpoint.Method, VatCustomerInformationEndpoint.Path, VatCustomerInformationEndpoint.Scope, Vrn, [], typeof(VatCustomerInformationRequest), typeof(VatCustomerInformationResponse), "statutory VAT registration", "RetrieveVatCustomerInformation"),
        Enquiry("vat.financial-details.retrieve", VatFinancialDetailsEndpoint.Method, VatFinancialDetailsEndpoint.Path, VatFinancialDetailsEndpoint.Scope, ["vrn", "penaltyChargeReference"], [], typeof(VatFinancialDetailsRequest), typeof(VatFinancialDetailsResponse), "VAT registration and penalty reference", "RetrieveVatFinancialDetails"),
        Enquiry("vat.liabilities.list", VatLiabilitiesEndpoint.Method, VatLiabilitiesEndpoint.Path, VatLiabilitiesEndpoint.Scope, Vrn, ["from", "to"], typeof(VatLiabilitiesRequest), typeof(VatLiabilitiesResponse), "VAT registration and liability period", "RetrieveVatLiabilities"),
        Enquiry("vat.obligations.list", VatObligationsEndpoint.Method, VatObligationsEndpoint.Path, VatObligationsEndpoint.Scope, Vrn, ["from", "to", "status"], typeof(VatObligationsRequest), typeof(VatObligationsResponse), "VAT registration and obligation period", "DescribeVatObligations", true, "/harness/hmrc/vat/obligations/describe"),
        Enquiry("vat.payments.list", VatPaymentsEndpoint.Method, VatPaymentsEndpoint.Path, VatPaymentsEndpoint.Scope, Vrn, ["from", "to"], typeof(VatPaymentsRequest), typeof(VatPaymentsResponse), "VAT registration and payment period", "RetrieveVatPayments"),
        Enquiry("vat.penalties.list", VatPenaltiesEndpoint.Method, VatPenaltiesEndpoint.Path, VatPenaltiesEndpoint.Scope, Vrn, [], typeof(VatPenaltiesRequest), typeof(VatPenaltiesResponse), "VAT registration", "RetrieveVatPenalties"),
        new("vat.returns.submit", VatReturnEndpoint.Method, VatReturnEndpoint.Path, VatReturnEndpoint.Version, VatReturnEndpoint.Scope, Vrn, [], VatRequestShape.AccountingBody, typeof(VatReturnRequest), typeof(VatReturnResponse), Accept, "application/json", VatAccountsModeDecision.Supported, "nine-box VAT accounting projection", "PrepareVatReturn", "/api/vat/returns", true, true, true),
        Enquiry("vat.returns.retrieve", VatViewReturnEndpoint.Method, VatViewReturnEndpoint.Path, VatViewReturnEndpoint.Scope, ["vrn", "periodKey"], [], typeof(VatViewReturnRequest), typeof(VatViewReturnResponse), "VAT registration and period key", "DescribeVatReturn", true, "/harness/hmrc/vat/returns/view/describe")
    ];

    private static VatOperationDescriptor Enquiry(string id, string method, string path, string scope,
        IReadOnlyList<string> pathParameters, IReadOnlyList<string> queryParameters, Type request, Type response,
        string source, string useCase, bool approved = false, string? route = null) => new(id, method, path, "1.0", scope,
            pathParameters, queryParameters, VatRequestShape.BodylessEnquiry, request, response, Accept, null,
            approved ? VatAccountsModeDecision.Supported : VatAccountsModeDecision.Deferred,
            source, useCase, route ?? $"/api/vat/{id[4..].Replace('.', '-')}", true, false, approved);
}

public static class VatJson
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static byte[] SerializeCanonical<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, CanonicalOptions);
    public static string Serialize<T>(T value, bool indented = false)
    {
        if (!indented) return JsonSerializer.Serialize(value, CanonicalOptions);
        return JsonSerializer.Serialize(value, new JsonSerializerOptions(CanonicalOptions) { WriteIndented = true });
    }
}
