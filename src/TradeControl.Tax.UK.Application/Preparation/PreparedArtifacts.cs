using System.Collections.Immutable;
using System.Security.Cryptography;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.Application.Preparation;

public enum PreparedArtifactStatus
{
    Preview,
    SubmissionReady
}

public enum PreparedFindingSeverity
{
    Information,
    Warning,
    Error
}

public sealed record PreparedArtifactFinding(
    PreparedFindingSeverity Severity,
    string Code,
    string Message,
    string? Path = null);

public sealed class PreparedStatutoryArtifact
{
    private PreparedStatutoryArtifact(
        string jurisdictionCode,
        string authorityCode,
        string operationCode,
        string contractVersion,
        PreparedArtifactStatus status,
        string mediaType,
        ImmutableArray<byte> content,
        ImmutableArray<SourceVersion> sourceEvidence,
        ImmutableArray<PreparedArtifactFinding> findings)
    {
        JurisdictionCode = jurisdictionCode;
        AuthorityCode = authorityCode;
        OperationCode = operationCode;
        ContractVersion = contractVersion;
        Status = status;
        MediaType = mediaType;
        Content = content;
        Sha256 = Convert.ToHexString(SHA256.HashData(content.AsSpan()));
        SourceEvidence = sourceEvidence;
        Findings = findings;
    }

    public string JurisdictionCode { get; }
    public string AuthorityCode { get; }
    public string OperationCode { get; }
    public string ContractVersion { get; }
    public PreparedArtifactStatus Status { get; }
    public string MediaType { get; }
    public ImmutableArray<byte> Content { get; }
    public string Sha256 { get; }
    public ImmutableArray<SourceVersion> SourceEvidence { get; }
    public ImmutableArray<PreparedArtifactFinding> Findings { get; }
    public bool HasErrors => Findings.Any(finding => finding.Severity == PreparedFindingSeverity.Error);

    public static PreparedStatutoryArtifact Create(
        string jurisdictionCode,
        string authorityCode,
        string operationCode,
        string contractVersion,
        PreparedArtifactStatus status,
        string mediaType,
        ReadOnlySpan<byte> content,
        IEnumerable<SourceVersion>? sourceEvidence = null,
        IEnumerable<PreparedArtifactFinding>? findings = null)
    {
        Require(jurisdictionCode, nameof(jurisdictionCode));
        Require(authorityCode, nameof(authorityCode));
        Require(operationCode, nameof(operationCode));
        Require(contractVersion, nameof(contractVersion));
        Require(mediaType, nameof(mediaType));

        return new(
            jurisdictionCode,
            authorityCode,
            operationCode,
            contractVersion,
            status,
            mediaType,
            ImmutableArray.Create(content.ToArray()),
            sourceEvidence?.ToImmutableArray() ?? [],
            findings?.ToImmutableArray() ?? []);
    }

    private static void Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A prepared artifact identifier cannot be empty.", parameterName);
    }
}

public sealed record PreparedNameValue(string Name, string Value);

public sealed class PreparedApiRequest
{
    private static readonly HashSet<string> CredentialHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization"
    };

    public PreparedApiRequest(
        PreparedStatutoryArtifact artifact,
        string method,
        string relativePath,
        IEnumerable<PreparedNameValue>? query = null,
        IEnumerable<PreparedNameValue>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (string.IsNullOrWhiteSpace(method))
            throw new ArgumentException("The HTTP method cannot be empty.", nameof(method));
        if (string.IsNullOrWhiteSpace(relativePath)
            || Uri.TryCreate(relativePath, UriKind.Absolute, out _))
            throw new ArgumentException("A relative authority path is required.", nameof(relativePath));

        Artifact = artifact;
        Method = method.Trim().ToUpperInvariant();
        RelativePath = relativePath;
        Query = query?.ToImmutableArray() ?? [];
        Headers = headers?.ToImmutableArray() ?? [];

        if (Headers.Any(header => CredentialHeaders.Contains(header.Name)))
            throw new ArgumentException("Prepared requests cannot contain credentials.", nameof(headers));
    }

    public PreparedStatutoryArtifact Artifact { get; }
    public string Method { get; }
    public string RelativePath { get; }
    public ImmutableArray<PreparedNameValue> Query { get; }
    public ImmutableArray<PreparedNameValue> Headers { get; }
}

public enum SubmissionPollingMode
{
    None,
    PollUntilTerminal
}

public sealed record PreparedPollingSemantics(
    SubmissionPollingMode Mode,
    string? RelativeStatusPath = null);

public sealed record PreparedPackageDocument(
    string Name,
    PreparedStatutoryArtifact Artifact);

public sealed class PreparedSubmissionPackage
{
    public PreparedSubmissionPackage(
        string serviceCode,
        PreparedStatutoryArtifact transmission,
        IEnumerable<PreparedPackageDocument> documents,
        PreparedPollingSemantics polling)
    {
        if (string.IsNullOrWhiteSpace(serviceCode))
            throw new ArgumentException("The submission service code cannot be empty.", nameof(serviceCode));
        ArgumentNullException.ThrowIfNull(transmission);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(polling);

        ServiceCode = serviceCode;
        Transmission = transmission;
        Documents = documents.ToImmutableArray();
        Polling = polling;

        if (Documents.Any(document => string.IsNullOrWhiteSpace(document.Name)))
            throw new ArgumentException("Every package document requires a name.", nameof(documents));
        if (Documents.Select(document => document.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()
            != Documents.Length)
            throw new ArgumentException("Package document names must be unique.", nameof(documents));
        if (polling.RelativeStatusPath is not null
            && Uri.TryCreate(polling.RelativeStatusPath, UriKind.Absolute, out _))
            throw new ArgumentException("A polling status path must be relative.", nameof(polling));
    }

    public string ServiceCode { get; }
    public PreparedStatutoryArtifact Transmission { get; }
    public ImmutableArray<PreparedPackageDocument> Documents { get; }
    public PreparedPollingSemantics Polling { get; }
}

public interface IPreparedApiRequestGateway
{
    Task SendAsync(PreparedApiRequest request, CancellationToken cancellationToken = default);
}

public interface IPreparedSubmissionPackageGateway
{
    Task SendAsync(PreparedSubmissionPackage package, CancellationToken cancellationToken = default);
}
