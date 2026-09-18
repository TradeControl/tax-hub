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
        descriptor.Method,
        descriptor.PathTemplate,
        descriptor.PathParameters.Select(name => new PreparedParameterContract(name)).ToArray(),
        descriptor.QueryParameters.Select(name => new PreparedParameterContract(name, false)).ToArray(),
        descriptor.Accept,
        descriptor.ContentType,
        descriptor.Shape == VatRequestShape.AccountingBody);

    public static PreparedApiContract From(SaOperationCoverage coverage)
    {
        var descriptor = coverage.Descriptor;
        return new(
            coverage.OperationId,
            coverage.ContractFamily,
            descriptor.ApiVersion,
            descriptor.Preview,
            descriptor.Method,
            descriptor.PathTemplate,
            descriptor.PathParameters.Select(item => new PreparedParameterContract(item.Name, item.Required)).ToArray(),
            descriptor.QueryParameters.Select(item => new PreparedParameterContract(item.Name, item.Required)).ToArray(),
            descriptor.Accept,
            descriptor.ContentType,
            descriptor.HasRequestBody);
    }
}
