using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.Company.Xbrl;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

namespace TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;

public sealed record CompaniesHouseMaterializedEnvelope(
    ImmutableArray<byte> Content,
    string PreviewSha256,
    string WireSha256,
    string AccountsSha256,
    string EvidenceClass)
{
    public const string MaterializedEvidence = "MATERIALISED — NOT SENT";
}

public sealed record CompaniesHouseMaterializedGatewayRequest(
    CompaniesHouseGatewayOperation Operation,
    ImmutableArray<byte> Content,
    string WireSha256,
    string EvidenceClass)
{
    public const string MaterializedEvidence = "MATERIALISED — NOT SENT";
}

public sealed class CompaniesHouseEnvelopeMaterializer(ISecretProvider secrets)
{
    private static readonly XNamespace GovTalk = CompaniesHouseEnvelopeSerializer.GovTalkNamespace;
    private static readonly XNamespace Form = CompaniesHouseEnvelopeSerializer.FormSubmissionNamespace;
    private static readonly HashSet<string> PermittedPreviewBlockers = new(StringComparer.Ordinal)
    {
        "CH-PENDING-CREDENTIALS",
        "CH-PENDING-DEVELOPER-TEST"
    };

    public async Task<CompaniesHouseMaterializedEnvelope> MaterializeTestAsync(
        PreparedSubmissionPackage package,
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePackage(package, transactionId);

        using var presenterId = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestPresenterId, cancellationToken);
        using var presenterAuthentication = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestPresenterAuthentication, cancellationToken);
        using var testFlagSecret = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestFlag, cancellationToken);
        using var packageReferenceSecret = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestPackageReference, cancellationToken);
        using var companyAuthenticationSecret = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.CompanyAuthentication, cancellationToken);

        var senderIdHash = Md5Hex(presenterId);
        var authenticationHash = Md5Hex(presenterAuthentication);
        var testFlag = Copy(testFlagSecret);
        var packageReference = Copy(packageReferenceSecret);
        var companyAuthentication = Copy(companyAuthenticationSecret);
        if (testFlag != "1")
            throw new InvalidOperationException("The Companies House official-test materialiser requires test flag 1.");
        if (packageReference.Length != 4 || packageReference.Any(value => !char.IsDigit(value)))
            throw new InvalidOperationException("The Companies House test package reference must contain four digits.");
        if (companyAuthentication.Length is < 6 or > 8
            || companyAuthentication.Any(value => !char.IsAsciiLetterOrDigit(value)))
            throw new InvalidOperationException("The company authentication code must contain six to eight letters or digits.");

        using var input = new MemoryStream(package.Transmission.Content.ToArray(), writable: false);
        var document = SecureXml.Parse(input);
        var root = document.Root ?? throw new InvalidDataException("The Companies House preview has no document element.");
        var messageDetails = Required(root.Element(GovTalk + "Header"), GovTalk + "MessageDetails");
        var header = Required(root, GovTalk + "Header");
        var formHeader = Required(root.Descendants(Form + "FormSubmission").SingleOrDefault(), Form + "FormHeader");
        if (header.Element(GovTalk + "SenderDetails") is not null
            || messageDetails.Element(GovTalk + "TransactionID") is not null
            || messageDetails.Element(GovTalk + "GatewayTest") is not null
            || formHeader.Element(Form + "CompanyAuthenticationCode") is not null)
            throw new InvalidOperationException("The prepared Companies House preview already contains transport-time values.");

        AddTransportHeader(header, messageDetails, transactionId, testFlag, senderIdHash, authenticationHash);

        var packageReferenceElement = Required(formHeader, Form + "PackageReference");
        packageReferenceElement.Value = packageReference;
        packageReferenceElement.AddBeforeSelf(
            new XElement(Form + "CompanyAuthenticationCode", companyAuthentication));

        var accounts = package.Documents.Single().Artifact;
        var encodedAccounts = Required(root.Descendants(Form + "Document").SingleOrDefault(), Form + "Data").Value;
        byte[] decodedAccounts;
        try
        {
            decodedAccounts = Convert.FromBase64String(encodedAccounts);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The Companies House accounts attachment is not valid base64.", exception);
        }
        if (!decodedAccounts.AsSpan().SequenceEqual(accounts.Content.AsSpan())
            || Convert.ToHexString(SHA256.HashData(decodedAccounts)) != accounts.Sha256)
            throw new InvalidOperationException("Credential materialisation changed the prepared accounts attachment.");

        var content = Serialize(document);
        return new(ImmutableArray.CreateRange(content), package.Transmission.Sha256,
            Convert.ToHexString(SHA256.HashData(content)), accounts.Sha256,
            CompaniesHouseMaterializedEnvelope.MaterializedEvidence);
    }

    public async Task<CompaniesHouseMaterializedGatewayRequest> MaterializeStatusRequestTestAsync(
        CompaniesHouseStatusQueryScope scope,
        string transactionId,
        string? submissionNumber = null,
        string? companyNumber = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTransactionId(transactionId);

        using var presenterIdSecret = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestPresenterId, cancellationToken);
        using var presenterAuthentication = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestPresenterAuthentication, cancellationToken);
        using var testFlagSecret = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestFlag, cancellationToken);

        var presenterId = Copy(presenterIdSecret);
        var preview = new CompaniesHouseEnvelopeSerializer().SerializeStatusRequest(
            new CompaniesHouseSubmissionStatusRequest(presenterId, scope, submissionNumber, companyNumber));
        return MaterializeGatewayRequest(preview, CompaniesHouseGatewayOperation.GetSubmissionStatus,
            transactionId, CopyAndValidateTestFlag(testFlagSecret), Md5Hex(presenterIdSecret),
            Md5Hex(presenterAuthentication));
    }

    public async Task<CompaniesHouseMaterializedGatewayRequest> MaterializeStatusAcknowledgementTestAsync(
        string transactionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateTransactionId(transactionId);

        using var presenterId = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestPresenterId, cancellationToken);
        using var presenterAuthentication = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestPresenterAuthentication, cancellationToken);
        using var testFlagSecret = await secrets.ResolveAsync(
            CompaniesHouseSecretReferences.TestFlag, cancellationToken);

        var preview = new CompaniesHouseEnvelopeSerializer().SerializeStatusAcknowledgement();
        return MaterializeGatewayRequest(preview, CompaniesHouseGatewayOperation.StatusAck,
            transactionId, CopyAndValidateTestFlag(testFlagSecret), Md5Hex(presenterId),
            Md5Hex(presenterAuthentication));
    }

    private static void ValidatePackage(PreparedSubmissionPackage package, string transactionId)
    {
        ValidateTransactionId(transactionId);
        var artifacts = package.Documents.Select(value => value.Artifact).Prepend(package.Transmission).ToArray();
        var unexpectedErrors = artifacts.SelectMany(value => value.Findings)
            .Where(value => value.Severity == PreparedFindingSeverity.Error
                && !PermittedPreviewBlockers.Contains(value.Code))
            .ToArray();
        if (package.ServiceCode != "COMPANIES-HOUSE-ACCOUNTS"
            || package.Transmission.ContractVersion != "TIS-6.0"
            || package.Transmission.Status != PreparedArtifactStatus.Preview
            || package.Polling.Mode != SubmissionPollingMode.CompaniesHouseXmlGateway
            || package.Polling.RelativeStatusPath != "GetSubmissionStatus"
            || package.Documents.Length != 1
            || unexpectedErrors.Length != 0)
            throw new InvalidOperationException("The prepared Companies House package is not eligible for test-envelope materialisation.");
    }

    private static CompaniesHouseMaterializedGatewayRequest MaterializeGatewayRequest(
        DocumentArtifact preview,
        CompaniesHouseGatewayOperation operation,
        string transactionId,
        string testFlag,
        string senderIdHash,
        string authenticationHash)
    {
        using var input = new MemoryStream(preview.Content.ToArray(), writable: false);
        var document = SecureXml.Parse(input);
        var root = document.Root ?? throw new InvalidDataException("The Companies House preview has no document element.");
        var header = Required(root, GovTalk + "Header");
        var messageDetails = Required(header, GovTalk + "MessageDetails");
        if (header.Element(GovTalk + "SenderDetails") is not null
            || messageDetails.Element(GovTalk + "TransactionID") is not null
            || messageDetails.Element(GovTalk + "GatewayTest") is not null)
            throw new InvalidOperationException("The prepared Companies House preview already contains transport-time values.");

        AddTransportHeader(header, messageDetails, transactionId, testFlag, senderIdHash, authenticationHash);
        var content = Serialize(document);
        return new(operation, ImmutableArray.CreateRange(content),
            Convert.ToHexString(SHA256.HashData(content)),
            CompaniesHouseMaterializedGatewayRequest.MaterializedEvidence);
    }

    private static void AddTransportHeader(XElement header, XElement messageDetails, string transactionId,
        string testFlag, string senderIdHash, string authenticationHash)
    {
        var transportAnchor = messageDetails.Element(GovTalk + "Function")
            ?? Required(messageDetails, GovTalk + "Qualifier");
        transportAnchor.AddAfterSelf(
            new XElement(GovTalk + "TransactionID", transactionId),
            new XElement(GovTalk + "GatewayTest", testFlag));
        messageDetails.AddAfterSelf(new XElement(GovTalk + "SenderDetails",
            new XElement(GovTalk + "IDAuthentication",
                new XElement(GovTalk + "SenderID", senderIdHash),
                new XElement(GovTalk + "Authentication",
                    new XElement(GovTalk + "Method", "clear"),
                    new XElement(GovTalk + "Value", authenticationHash)))));
    }

    private static void ValidateTransactionId(string transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > 32
            || transactionId.Any(value => !char.IsAsciiLetterOrDigit(value)))
            throw new ArgumentException("A bounded alphanumeric Companies House transaction ID is required.", nameof(transactionId));
    }

    private static string CopyAndValidateTestFlag(ProtectedSecret testFlagSecret)
    {
        var testFlag = Copy(testFlagSecret);
        if (testFlag != "1")
            throw new InvalidOperationException("The Companies House official-test materialiser requires test flag 1.");
        return testFlag;
    }

    private static XElement Required(XElement? parent, XName name) =>
        parent?.Element(name) ?? throw new InvalidDataException($"The Companies House preview is missing {name.LocalName}.");

    private static byte[] Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NewLineHandling = NewLineHandling.None
        }))
            document.Save(writer);
        return stream.ToArray();
    }

    private static string Md5Hex(ProtectedSecret secret)
    {
        var characters = new char[secret.Length];
        var bytes = new byte[Encoding.UTF8.GetMaxByteCount(characters.Length)];
        try
        {
            secret.CopyTo(characters);
            var count = Encoding.UTF8.GetBytes(characters, bytes);
            return Convert.ToHexString(MD5.HashData(bytes.AsSpan(0, count))).ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static string Copy(ProtectedSecret secret)
    {
        var characters = new char[secret.Length];
        try
        {
            secret.CopyTo(characters);
            return new string(characters);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(characters.AsSpan()));
        }
    }
}
