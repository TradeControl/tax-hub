using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;
using TradeControl.Tax.UK.Hmrc.Vat;

namespace TradeControl.Tax.UK.Application.Preparation;

public static class HmrcPreparedApiContracts
{
    public static PreparedApiContract From(VatOperationDescriptor descriptor) => new(
        descriptor.OperationId,
        "VAT",
        descriptor.ApiVersion,
        false,
        descriptor.AccountsMode switch
        {
            VatAccountsModeDecision.Supported => PreparedApiEligibility.Supported,
            VatAccountsModeDecision.Deferred => PreparedApiEligibility.Deferred,
            _ => PreparedApiEligibility.Unsupported
        },
        descriptor.Method,
        descriptor.PathTemplate,
        descriptor.PathParameters.Select(name => new PreparedParameterContract(name)).ToArray(),
        descriptor.QueryParameters.Select(name => new PreparedParameterContract(name, false)).ToArray(),
        descriptor.Accept,
        descriptor.ContentType,
        descriptor.Shape == VatRequestShape.AccountingBody,
        descriptor.OAuthScope,
        descriptor.SuccessStatusCode,
        PreparedApiResponseBodyExpectation.Json,
        descriptor.ResponseType);

    public static PreparedApiContract From(SaOperationCoverage coverage)
    {
        var descriptor = coverage.Descriptor;
        return new(
            coverage.OperationId,
            coverage.ContractFamily,
            descriptor.ApiVersion,
            descriptor.Preview,
            coverage.AccountsMode switch
            {
                SaAccountsModeDecision.Supported => PreparedApiEligibility.Supported,
                SaAccountsModeDecision.Deferred => PreparedApiEligibility.Deferred,
                _ => PreparedApiEligibility.Unsupported
            },
            descriptor.Method,
            descriptor.PathTemplate,
            descriptor.PathParameters.Select(item => new PreparedParameterContract(item.Name, item.Required)).ToArray(),
            descriptor.QueryParameters.Select(item => new PreparedParameterContract(item.Name, item.Required)).ToArray(),
            descriptor.Accept,
            descriptor.ContentType,
            descriptor.HasRequestBody,
            descriptor.OAuthScope,
            descriptor.SuccessStatusCode,
            descriptor.ResponseType is null
                ? PreparedApiResponseBodyExpectation.None
                : PreparedApiResponseBodyExpectation.Json,
            descriptor.ResponseType);
    }
}
