using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public enum CorporationTaxValidationFacility
{
    LocalTestService,
    ThirdPartyValidationService
}

public sealed record CorporationTaxValidationFinding(
    string? RaisedBy,
    string? Number,
    string? Type,
    string? Text,
    string? Location);

public sealed record CorporationTaxValidationEvidence(
    CorporationTaxValidationFacility Facility,
    string EvidenceClassification,
    bool IsExternalHmrcResponse,
    string Endpoint,
    string HttpMethod,
    string RequestContentType,
    int HttpStatusCode,
    string? ResponseContentType,
    DateTimeOffset RequestedAtUtc,
    long DurationMilliseconds,
    string ServiceArtifactSha256,
    string RequestBodySha256,
    string ResponseBodySha256,
    string EvidenceDirectory,
    IReadOnlyDictionary<string, string[]> ResponseHeaders,
    IReadOnlyList<CorporationTaxValidationFinding> Findings,
    string ExactRequestBody,
    string ExactResponseBody);

public sealed class CorporationTaxValidationOptions
{
    public const string SectionName = "TaxHub:CorporationTaxValidation";
    public bool LtsEnabled { get; init; }
    public bool TpvsEnabled { get; init; }
    public string LtsEndpoint { get; init; } = "http://127.0.0.1:5665/LTS/LTSPostServlet";
    public string TpvsEndpoint { get; init; } = "https://www.tpvs.hmrc.gov.uk/HMRC/CT600";
    public int TimeoutSeconds { get; init; } = 180;
}

public interface ICorporationTaxValidationDiagnostics
{
    Task<CorporationTaxValidationEvidence> ValidateLtsAsync(
        PreparedStatutoryArtifact serviceArtifact,
        CancellationToken cancellationToken = default);

    Task<CorporationTaxValidationEvidence> ValidateTpvsAsync(
        PreparedStatutoryArtifact serviceArtifact,
        CancellationToken cancellationToken = default);
}

public sealed class DisabledCorporationTaxValidationDiagnostics : ICorporationTaxValidationDiagnostics
{
    private static InvalidOperationException Disabled() => new(
        "Corporation Tax validation diagnostics are available only in an explicitly enabled Development WebHarness.");

    public Task<CorporationTaxValidationEvidence> ValidateLtsAsync(
        PreparedStatutoryArtifact serviceArtifact,
        CancellationToken cancellationToken = default) => throw Disabled();

    public Task<CorporationTaxValidationEvidence> ValidateTpvsAsync(
        PreparedStatutoryArtifact serviceArtifact,
        CancellationToken cancellationToken = default) => throw Disabled();
}

public sealed class CorporationTaxValidationDiagnostics : ICorporationTaxValidationDiagnostics
{
    internal const string LtsClassification = "LOCAL HMRC LTS RESULT — NOT A REMOTE HMRC RESPONSE";
    internal const string TpvsClassification = "GENUINE HMRC TPVS RESPONSE";
    private static readonly XNamespace GovTalk = "http://www.govtalk.gov.uk/CM/envelope";
    private readonly HttpClient _httpClient;
    private readonly CorporationTaxValidationOptions _options;
    private readonly string _evidenceRoot;
    private readonly TimeProvider _clock;

    public CorporationTaxValidationDiagnostics(
        HttpClient httpClient,
        CorporationTaxValidationOptions options,
        string evidenceRoot,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(evidenceRoot))
            throw new ArgumentException("An evidence root is required.", nameof(evidenceRoot));
        if (options.TimeoutSeconds is < 1 or > 600)
            throw new ArgumentOutOfRangeException(nameof(options), "The validation timeout must be between 1 and 600 seconds.");

        ValidateLtsEndpoint(new Uri(options.LtsEndpoint, UriKind.Absolute));
        ValidateTpvsEndpoint(new Uri(options.TpvsEndpoint, UriKind.Absolute));
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        _options = options;
        _evidenceRoot = Path.GetFullPath(evidenceRoot);
        _clock = clock ?? TimeProvider.System;
    }

    public Task<CorporationTaxValidationEvidence> ValidateLtsAsync(
        PreparedStatutoryArtifact serviceArtifact,
        CancellationToken cancellationToken = default)
    {
        if (!_options.LtsEnabled)
            throw new InvalidOperationException("The Local Test Service diagnostic is not enabled.");
        return ValidateAsync(serviceArtifact, CorporationTaxValidationFacility.LocalTestService,
            new Uri(_options.LtsEndpoint, UriKind.Absolute), LtsClassification, false, cancellationToken);
    }

    public Task<CorporationTaxValidationEvidence> ValidateTpvsAsync(
        PreparedStatutoryArtifact serviceArtifact,
        CancellationToken cancellationToken = default)
    {
        if (!_options.TpvsEnabled)
            throw new InvalidOperationException("The external HMRC TPVS diagnostic is not enabled.");
        return ValidateAsync(serviceArtifact, CorporationTaxValidationFacility.ThirdPartyValidationService,
            new Uri(_options.TpvsEndpoint, UriKind.Absolute), TpvsClassification, true, cancellationToken);
    }

    private async Task<CorporationTaxValidationEvidence> ValidateAsync(
        PreparedStatutoryArtifact serviceArtifact,
        CorporationTaxValidationFacility facility,
        Uri endpoint,
        string classification,
        bool external,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceArtifact);
        cancellationToken.ThrowIfCancellationRequested();
        if (serviceArtifact.OperationCode != "HMRC-CT600-SERVICE-ROOT"
            || serviceArtifact.AuthorityCode != "HMRC"
            || serviceArtifact.HasErrors)
            throw new InvalidOperationException("Only an error-free prepared HMRC Corporation Tax service artifact can be validated.");

        var artifactBytes = serviceArtifact.Content.ToArray();
        var actualArtifactSha = Sha256(artifactBytes);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualArtifactSha), Convert.FromHexString(serviceArtifact.Sha256)))
            throw new InvalidOperationException("The prepared Corporation Tax service artifact no longer matches its digest.");

        var requestBytes = CorporationTaxValidationEnvelope.Wrap(artifactBytes);
        var requestSha = Sha256(requestBytes);
        var requestedAt = _clock.GetUtcNow();
        var evidenceDirectory = CreateEvidenceDirectory(requestedAt, facility, requestSha);
        await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, "service-artifact.xml"), artifactBytes, cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, "request.xml"), requestBytes, cancellationToken);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(requestBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml") { CharSet = "utf-8" };
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            stopwatch.Stop();
            var responseSha = Sha256(responseBytes);
            var responseHeaders = response.Headers.Concat(response.Content.Headers)
                .GroupBy(header => header.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.SelectMany(value => value.Value).ToArray(),
                    StringComparer.OrdinalIgnoreCase);
            var responseText = Decode(responseBytes, response.Content.Headers.ContentType?.CharSet);
            var evidence = new CorporationTaxValidationEvidence(
                facility, classification, external, endpoint.AbsoluteUri, "POST", "application/xml; charset=utf-8",
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString(), requestedAt, stopwatch.ElapsedMilliseconds,
                actualArtifactSha, requestSha, responseSha, evidenceDirectory, responseHeaders,
                ParseFindings(responseText), Encoding.UTF8.GetString(requestBytes), responseText);
            await File.WriteAllBytesAsync(Path.Combine(evidenceDirectory, "response.bin"), responseBytes,
                cancellationToken);
            await WriteMetadataAsync(evidenceDirectory, evidence, null, cancellationToken);
            return evidence;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
                                          && exception is HttpRequestException or TaskCanceledException)
        {
            stopwatch.Stop();
            await WriteMetadataAsync(evidenceDirectory, null, new
            {
                facility,
                classification,
                endpoint = endpoint.AbsoluteUri,
                requestedAtUtc = requestedAt,
                durationMilliseconds = stopwatch.ElapsedMilliseconds,
                serviceArtifactSha256 = actualArtifactSha,
                requestBodySha256 = requestSha,
                errorType = exception.GetType().Name,
                error = exception.Message
            }, CancellationToken.None);
            throw new InvalidOperationException(
                $"The {facility} diagnostic could not reach its configured validation endpoint. Request evidence was retained at '{evidenceDirectory}'.",
                exception);
        }
    }

    private string CreateEvidenceDirectory(DateTimeOffset requestedAt,
        CorporationTaxValidationFacility facility, string requestSha)
    {
        Directory.CreateDirectory(_evidenceRoot);
        var facilityName = facility == CorporationTaxValidationFacility.LocalTestService ? "lts" : "tpvs";
        var leaf = $"{requestedAt:yyyyMMddTHHmmssfffZ}-{facilityName}-{requestSha[..12]}-{Guid.NewGuid():N}";
        var directory = Path.GetFullPath(Path.Combine(_evidenceRoot, leaf));
        if (!directory.StartsWith(_evidenceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The validation evidence directory escaped its configured root.");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task WriteMetadataAsync(string directory, object? evidence, object? failure,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evidence ?? failure!, new JsonSerializerOptions
        {
            WriteIndented = true
        });
        await File.WriteAllBytesAsync(Path.Combine(directory, "metadata.json"), bytes, cancellationToken);
    }

    private static IReadOnlyList<CorporationTaxValidationFinding> ParseFindings(string response)
    {
        try
        {
            var document = XDocument.Parse(response, LoadOptions.PreserveWhitespace);
            return document.Descendants().Where(element => element.Name.LocalName == "Error")
                .Select(error => new CorporationTaxValidationFinding(
                    Child(error, "RaisedBy"), Child(error, "Number"), Child(error, "Type"),
                    Child(error, "Text"), Child(error, "Location")))
                .ToArray();
        }
        catch (Exception exception) when (exception is System.Xml.XmlException or InvalidOperationException)
        {
            return [];
        }
    }

    private static string? Child(XElement element, string localName) => element.Elements()
        .FirstOrDefault(child => child.Name.LocalName == localName)?.Value;

    private static string Decode(byte[] bytes, string? charset)
    {
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try { return Encoding.GetEncoding(charset.Trim('"')).GetString(bytes); }
            catch (ArgumentException) { }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static string Sha256(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void ValidateLtsEndpoint(Uri endpoint)
    {
        if (!endpoint.IsLoopback || endpoint.Scheme != Uri.UriSchemeHttp
            || !endpoint.AbsolutePath.Equals("/LTS/LTSPostServlet", StringComparison.Ordinal))
            throw new ArgumentException("The LTS endpoint must be the loopback HTTP LTSPostServlet endpoint.");
    }

    private static void ValidateTpvsEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttps
            || !endpoint.Host.Equals("www.tpvs.hmrc.gov.uk", StringComparison.OrdinalIgnoreCase)
            || !endpoint.AbsolutePath.Equals("/HMRC/CT600", StringComparison.Ordinal))
            throw new ArgumentException("The TPVS endpoint must be HMRC's published HTTPS CT600 endpoint.");
    }

    private static class CorporationTaxValidationEnvelope
    {
        public static byte[] Wrap(byte[] serviceArtifact)
        {
            XDocument service;
            try
            {
                service = XDocument.Parse(Encoding.UTF8.GetString(serviceArtifact), LoadOptions.PreserveWhitespace);
            }
            catch (System.Xml.XmlException exception)
            {
                throw new InvalidOperationException("The prepared Corporation Tax service artifact is not XML.", exception);
            }
            var root = service.Root ?? throw new InvalidOperationException("The prepared service artifact has no root element.");
            if (root.Name != XName.Get("IRenvelope", "http://www.govtalk.gov.uk/taxation/CT/5"))
                throw new InvalidOperationException("The prepared service artifact is not the pinned CT600 IRenvelope.");
            var utr = root.Descendants().FirstOrDefault(element =>
                element.Name.LocalName == "Key"
                && (string?)element.Attribute("Type") == "UTR")?.Value;
            if (string.IsNullOrWhiteSpace(utr))
                throw new InvalidOperationException("The prepared service artifact does not contain its UTR key.");

            var envelope = new XDocument(new XDeclaration("1.0", "utf-8", null),
                new XElement(GovTalk + "GovTalkMessage",
                    new XElement(GovTalk + "EnvelopeVersion", "2.0"),
                    new XElement(GovTalk + "Header",
                        new XElement(GovTalk + "MessageDetails",
                            new XElement(GovTalk + "Class", "HMRC-CT-CT600"),
                            new XElement(GovTalk + "Qualifier", "request"),
                            new XElement(GovTalk + "Function", "submit")),
                        new XElement(GovTalk + "SenderDetails",
                            new XElement(GovTalk + "IDAuthentication",
                                new XElement(GovTalk + "SenderID", "webharness-validation"),
                                new XElement(GovTalk + "Authentication",
                                    new XElement(GovTalk + "Method", "clear"),
                                    new XElement(GovTalk + "Value", "validation-only"))))),
                    new XElement(GovTalk + "GovTalkDetails",
                        new XElement(GovTalk + "Keys", new XElement(GovTalk + "Key",
                            new XAttribute("Type", "UTR"), utr))),
                    new XElement(GovTalk + "Body", root)));
            return Encoding.UTF8.GetBytes(envelope.ToString(SaveOptions.DisableFormatting));
        }
    }
}
