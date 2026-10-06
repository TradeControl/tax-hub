using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Xml;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

namespace TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;

public static class CompaniesHouseGatewayEndpoints
{
    public static Uri OfficialTest { get; } =
        new("https://xmlgw.companieshouse.gov.uk/v1-0/xmlgw/Gateway", UriKind.Absolute);

    public static bool IsOfficialTest(Uri uri) => uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && uri.IdnHost == OfficialTest.IdnHost
        && uri.AbsolutePath == OfficialTest.AbsolutePath
        && string.IsNullOrEmpty(uri.Query)
        && string.IsNullOrEmpty(uri.Fragment);
}

public static class CompaniesHouseOfficialTestGatewayFactory
{
    public const string OfficialTestEvidence = "OFFICIAL COMPANIES HOUSE TEST GATEWAY";

    public static ICompaniesHouseXmlGatewaySession Create(CompaniesHouseHttpGatewayOptions? options = null)
    {
        var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        return new CompaniesHouseOfficialTestHttpGateway(http, options, OfficialTestEvidence);
    }
}

public sealed record CompaniesHouseHttpGatewayOptions(
    int MaximumResponseBytes = 1_048_576,
    TimeSpan? RequestTimeout = null)
{
    internal TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaximumResponseBytes is < 1 or > 4_194_304)
            throw new ArgumentException("The Companies House response bound is invalid.");
        if (EffectiveRequestTimeout <= TimeSpan.Zero || EffectiveRequestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("The Companies House request timeout is invalid.");
    }
}

/// <summary>
/// Production-shaped test transport. It remains internal and is not composed by any host;
/// Increment 3 exercises it only with a recording HTTP handler.
/// </summary>
internal sealed class CompaniesHouseOfficialTestHttpGateway : ICompaniesHouseXmlGatewaySession
{
    internal const string OfflineEvidence = "OFFICIAL CONTRACT — OFFLINE HTTP — NOT FILED";
    private readonly HttpClient _http;
    private readonly CompaniesHouseHttpGatewayOptions _options;
    private readonly string _evidenceClass;

    internal CompaniesHouseOfficialTestHttpGateway(HttpClient http,
        CompaniesHouseHttpGatewayOptions? options = null,
        string? evidenceClass = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new();
        _options.Validate();
        _evidenceClass = evidenceClass ?? OfflineEvidence;
    }

    public string EvidenceClass => _evidenceClass;

    public async Task<CompaniesHouseGatewayResponse> ExchangeAsync(
        CompaniesHouseGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(HttpMethod.Post, CompaniesHouseGatewayEndpoints.OfficialTest)
        {
            Content = new ByteArrayContent(request.Content.ToArray())
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml")
        {
            CharSet = "utf-8"
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.EffectiveRequestTimeout);
        using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.RequestMessage?.RequestUri is not { } finalUri
            || !CompaniesHouseGatewayEndpoints.IsOfficialTest(finalUri))
            throw new InvalidOperationException("The Companies House response originated from an unapproved endpoint.");
        var responseBytes = await ReadBoundedAsync(response.Content, timeout.Token);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException("The Companies House gateway returned a non-success HTTP status.",
                null, response.StatusCode);
        CompaniesHouseGatewayResponseKind kind;
        try
        {
            kind = Classify(request.Operation, responseBytes);
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            throw new CompaniesHouseUnclassifiedResponseException(exception.Message,
                ImmutableArray.CreateRange(responseBytes), Hash(responseBytes), exception);
        }
        return new(kind, ImmutableArray.CreateRange(responseBytes),
            Hash(request.Content.AsSpan()), Hash(responseBytes), _evidenceClass);
    }

    private static CompaniesHouseGatewayResponseKind Classify(
        CompaniesHouseGatewayOperation operation,
        byte[] response)
    {
        using var stream = new MemoryStream(response, writable: false);
        var document = SecureXml.Parse(stream);
        var root = document.Root;
        if (root is null || root.Name.LocalName != "GovTalkMessage")
            throw new InvalidDataException("The Companies House response is not a GovTalk message.");
        var qualifier = root.Descendants().SingleOrDefault(element => element.Name.LocalName == "Qualifier")?.Value;
        var hasErrors = root.Descendants().Any(element => element.Name.LocalName == "GovTalkErrors");
        var messageClass = root.Descendants().SingleOrDefault(element => element.Name.LocalName == "Class")?.Value;
        var serializer = new CompaniesHouseEnvelopeSerializer();
        if (hasErrors || qualifier == "error")
        {
            _ = serializer.ParseGatewayAcknowledgement(response);
            return CompaniesHouseGatewayResponseKind.GatewayError;
        }
        if (operation == CompaniesHouseGatewayOperation.GetSubmissionStatus && qualifier == "response")
        {
            if (messageClass != "GetSubmissionStatus")
                throw new InvalidDataException("The Companies House status response class is unexpected.");
            _ = serializer.ParseSubmissionStatuses(response);
            return CompaniesHouseGatewayResponseKind.SubmissionStatus;
        }
        var isFormSubmissionReceipt = operation == CompaniesHouseGatewayOperation.FormSubmission
            && qualifier is "acknowledgement" or "response";
        if (qualifier == "acknowledgement" || isFormSubmissionReceipt)
        {
            var expectedClass = operation switch
            {
                CompaniesHouseGatewayOperation.FormSubmission => "Accounts",
                CompaniesHouseGatewayOperation.StatusAck => "StatusAck",
                _ => "GetSubmissionStatus"
            };
            if (messageClass != expectedClass)
                throw new InvalidDataException("The Companies House acknowledgement class is unexpected.");
            _ = serializer.ParseGatewayAcknowledgement(response);
            return operation == CompaniesHouseGatewayOperation.StatusAck
                ? CompaniesHouseGatewayResponseKind.StatusAcknowledgement
                : CompaniesHouseGatewayResponseKind.Acknowledgement;
        }
        throw new InvalidDataException("The Companies House response does not match the requested operation.");
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0
            && content.Headers.ContentLength > _options.MaximumResponseBytes)
            throw new InvalidDataException("The Companies House response exceeds the configured bound.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16_384];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            if (output.Length + read > _options.MaximumResponseBytes)
                throw new InvalidDataException("The Companies House response exceeds the configured bound.");
            output.Write(buffer, 0, read);
        }
        if (output.Length == 0)
            throw new InvalidDataException("The Companies House gateway returned an empty response.");
        return output.ToArray();
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));
}
