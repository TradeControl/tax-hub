using System.Collections.Immutable;

namespace TradeControl.Tax.UK.Application.Preparation;

public enum CompaniesHouseGatewayOperation
{
    FormSubmission,
    GetSubmissionStatus,
    StatusAck
}

public enum CompaniesHouseGatewayResponseKind
{
    Acknowledgement,
    SubmissionStatus,
    StatusAcknowledgement,
    GatewayError
}

public sealed record CompaniesHouseGatewayRequest(
    CompaniesHouseGatewayOperation Operation,
    ImmutableArray<byte> Content)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Operation) || Content.IsDefaultOrEmpty)
            throw new ArgumentException("A Companies House gateway operation and non-empty XML content are required.");
    }
}

public sealed record CompaniesHouseGatewayResponse(
    CompaniesHouseGatewayResponseKind Kind,
    ImmutableArray<byte> Content,
    string RequestSha256,
    string ResponseSha256,
    string EvidenceClass)
{
    public void Validate()
    {
        if (!Enum.IsDefined(Kind) || Content.IsDefaultOrEmpty
            || string.IsNullOrWhiteSpace(EvidenceClass)
            || RequestSha256.Length != 64 || ResponseSha256.Length != 64)
            throw new ArgumentException("The Companies House gateway response is incomplete.");
    }
}

public sealed class CompaniesHouseUnclassifiedResponseException : Exception
{
    public CompaniesHouseUnclassifiedResponseException(
        string message,
        ImmutableArray<byte> responseContent,
        string responseSha256,
        Exception innerException) : base(message, innerException)
    {
        if (responseContent.IsDefaultOrEmpty || responseSha256.Length != 64)
            throw new ArgumentException("The unclassified Companies House response evidence is incomplete.");
        ResponseContent = responseContent;
        ResponseSha256 = responseSha256;
    }

    public ImmutableArray<byte> ResponseContent { get; }
    public string ResponseSha256 { get; }
}

public interface ICompaniesHouseXmlGatewaySession
{
    string EvidenceClass { get; }

    Task<CompaniesHouseGatewayResponse> ExchangeAsync(
        CompaniesHouseGatewayRequest request,
        CancellationToken cancellationToken = default);
}
