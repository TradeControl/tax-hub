using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;

public sealed class SendDisabledCompaniesHouseOfficialTestGateway : ICompaniesHouseXmlGatewaySession
{
    public const string OfficialTestEvidence = "OFFICIAL COMPANIES HOUSE TEST — SEND DISABLED";

    public string EvidenceClass => OfficialTestEvidence;

    public Task<CompaniesHouseGatewayResponse> ExchangeAsync(
        CompaniesHouseGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "The official Companies House test gateway is structurally selected, but external sending remains disabled pending credential materialisation and human authorisation.");
    }
}
