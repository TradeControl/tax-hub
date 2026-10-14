using System.Collections.Immutable;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.Xml;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

internal sealed record TransactionEngineProtocolOptions(
    string MessageClass,
    Uri SubmissionEndpoint,
    IReadOnlySet<string> TrustedResponseHosts,
    int MaximumXmlBytes = 8 * 1024 * 1024,
    int RecoveryPollIntervalSeconds = 10)
{
    internal const string CorporationTaxClass = "HMRC-CT-CT600";
    internal static readonly Uri PublishedProductionEndpoint =
        new("https://transaction-engine.tax.service.gov.uk/submission");
    internal static readonly Uri PublishedExternalTestEndpoint =
        new("https://test-transaction-engine.tax.service.gov.uk/submission");

    internal static TransactionEngineProtocolOptions CorporationTaxFixture() => new(
        CorporationTaxClass,
        PublishedProductionEndpoint,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PublishedProductionEndpoint.Host,
            PublishedExternalTestEndpoint.Host
        });

    internal static TransactionEngineProtocolOptions CorporationTaxExternalTest() => new(
        CorporationTaxClass,
        PublishedExternalTestEndpoint,
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            PublishedExternalTestEndpoint.Host
        });
}

internal sealed record TransactionEngineSubmissionIdentity(
    string VendorId,
    string SenderId,
    string Password,
    string Utr,
    string Product,
    string ProductVersion,
    bool IsExternalTest = true);

internal sealed record TransactionEngineMaterializedSubmission(
    ImmutableArray<byte> Content,
    string TransactionId,
    string ServiceRootSha256,
    string WireSha256,
    string Irmark,
    ImmutableArray<string> AttachmentSha256,
    string EvidenceClass = "OFFICIAL-PROTOCOL-FIXTURE")
{
    public override string ToString() =>
        $"{EvidenceClass}:{ServiceRootSha256}:{WireSha256}";
}

internal static partial class TransactionEngineWire
{
    internal const string GovTalkNamespace = "http://www.govtalk.gov.uk/CM/envelope";
    internal const string CorporationTaxNamespace = "http://www.govtalk.gov.uk/taxation/CT/5";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    [GeneratedRegex(@"<(?<name>(?:[A-Za-z_][\w.-]*:)?IRmark)\b(?<attributes>[^<>]*?)(?:(?<self>/\s*>)|>(?<value>\s*)</\k<name>\s*>)",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex EmptyIrmarkPattern();

    internal static TransactionEngineMaterializedSubmission Materialize(
        PreparedSubmissionPackage package,
        TransactionEngineSubmissionIdentity identity,
        string transactionId,
        TransactionEngineProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(identity);
        ValidateOptions(options);
        ValidateIdentity(identity);
        ValidateTransactionId(transactionId);

        if (package.ServiceCode != "HMRC-CORPORATION-TAX"
            || package.Polling.Mode != SubmissionPollingMode.TransactionEngine)
            throw new InvalidOperationException("Only a prepared Corporation Tax Transaction Engine package is accepted.");
        if (package.Transmission.MediaType != "application/xml" || package.Transmission.Content.IsDefaultOrEmpty)
            throw new InvalidOperationException("The immutable CT service root must be non-empty XML.");
        if (package.Transmission.Content.Length > options.MaximumXmlBytes)
            throw new InvalidOperationException("The CT service root exceeds the configured XML bound.");
        if (package.Transmission.HasErrors)
            throw new InvalidOperationException("A CT service root with preparation errors cannot be materialised.");

        var serviceBytes = package.Transmission.Content.ToArray();
        var serviceDocument = LoadSecure(serviceBytes, options.MaximumXmlBytes);
        var root = serviceDocument.DocumentElement;
        if (root?.LocalName != "IRenvelope" || root.NamespaceURI != CorporationTaxNamespace)
            throw new InvalidOperationException("The prepared transmission is not the approved CT600 service root.");
        var irmarks = serviceDocument.GetElementsByTagName("IRmark", CorporationTaxNamespace)
            .OfType<XmlElement>().ToArray();
        if (irmarks.Length != 1 || irmarks[0].GetAttribute("Type") != "generic"
            || !string.IsNullOrWhiteSpace(irmarks[0].InnerText) || irmarks[0].ChildNodes.Count != 0)
            throw new InvalidOperationException("The CT service root must contain exactly one empty generic IRmark slot.");

        var serviceText = Utf8.GetString(serviceBytes);
        var embeddedServiceText = StripXmlDeclaration(serviceText);
        var matches = EmptyIrmarkPattern().Matches(embeddedServiceText);
        if (matches.Count != 1)
            throw new InvalidOperationException("The reserved IRmark slot cannot be identified without reserializing the service root.");

        var draft = BuildSubmission(embeddedServiceText, identity, transactionId, options);
        var irmark = CalculateIrmark(draft, options.MaximumXmlBytes);
        var match = matches[0];
        var name = match.Groups["name"].Value;
        var replacement = $"<{name}{match.Groups["attributes"].Value}>{irmark}</{name}>";
        var markedService = string.Concat(embeddedServiceText.AsSpan(0, match.Index), replacement,
            embeddedServiceText.AsSpan(match.Index + match.Length));
        var finalWire = BuildSubmission(markedService, identity, transactionId, options);

        VerifyOnlyIrmarkChanged(embeddedServiceText, markedService, irmark, options.MaximumXmlBytes);
        return new(
            ImmutableArray.Create(finalWire),
            transactionId,
            package.Transmission.Sha256,
            Convert.ToHexString(SHA256.HashData(finalWire)),
            irmark,
            package.Documents.Select(document => document.Artifact.Sha256).ToImmutableArray());
    }

    internal static string CalculateIrmark(ReadOnlySpan<byte> submission, int maximumXmlBytes = 8 * 1024 * 1024)
    {
        var document = LoadSecure(submission, maximumXmlBytes);
        var body = document.GetElementsByTagName("Body", GovTalkNamespace)
            .OfType<XmlElement>().SingleOrDefault()
            ?? throw new InvalidDataException("The GovTalk submission must contain exactly one Body element.");
        var irmarks = body.GetElementsByTagName("IRmark").OfType<XmlElement>()
            .Where(element => element.GetAttribute("Type") == "generic").ToArray();
        if (irmarks.Length != 1)
            throw new InvalidDataException("The GovTalk Body must contain exactly one generic IRmark element.");

        var canonicalDocument = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
        var importedBody = (XmlElement)canonicalDocument.ImportNode(body, true);
        canonicalDocument.AppendChild(importedBody);
        if (!importedBody.HasAttribute("xmlns"))
            importedBody.SetAttribute("xmlns", GovTalkNamespace);
        var importedIrmark = importedBody.GetElementsByTagName("IRmark").OfType<XmlElement>()
            .Single(element => element.GetAttribute("Type") == "generic");
        importedIrmark.ParentNode!.RemoveChild(importedIrmark);

        var transform = new XmlDsigC14NTransform(includeComments: false);
        transform.LoadInput(canonicalDocument);
        using var canonical = (Stream)transform.GetOutput(typeof(Stream));
        using var buffer = new MemoryStream();
        canonical.CopyTo(buffer);
        return Convert.ToBase64String(SHA1.HashData(buffer.ToArray()));
    }

    internal static byte[] Poll(string correlationId, TransactionEngineProtocolOptions options) =>
        ControlMessage("poll", correlationId, options);

    internal static byte[] Delete(string correlationId, TransactionEngineProtocolOptions options) =>
        ControlMessage("delete", correlationId, options);

    internal static byte[] DataRequest(TransactionEngineSubmissionIdentity identity,
        TransactionEngineProtocolOptions options)
    {
        ValidateOptions(options);
        ValidateIdentity(identity);
        return Encode($"""
            <?xml version="1.0" encoding="utf-8"?><GovTalkMessage xmlns="{GovTalkNamespace}"><EnvelopeVersion>2.0</EnvelopeVersion><Header><MessageDetails><Class>{Escape(options.MessageClass)}</Class><Qualifier>request</Qualifier><Function>list</Function><CorrelationID></CorrelationID><Transformation>XML</Transformation>{GatewayTest(identity)}</MessageDetails><SenderDetails>{Authentication(identity)}</SenderDetails></Header><GovTalkDetails><Keys></Keys></GovTalkDetails><Body><StatusRequest xmlns="urn:gateway/statusrequest"><IncludeIdentifiers>1</IncludeIdentifiers></StatusRequest></Body></GovTalkMessage>
            """);
    }

    internal static Uri ValidateResponseEndpoint(string value, TransactionEngineProtocolOptions options)
    {
        ValidateOptions(options);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || !endpoint.IsDefaultPort
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query)
            || !string.IsNullOrEmpty(endpoint.Fragment)
            || !options.TrustedResponseHosts.Contains(endpoint.IdnHost))
            throw new InvalidDataException("The Transaction Engine returned an untrusted response endpoint.");
        return endpoint;
    }

    internal static XmlDocument LoadSecure(ReadOnlySpan<byte> content, int maximumXmlBytes)
    {
        if (content.IsEmpty || content.Length > maximumXmlBytes)
            throw new InvalidDataException("XML is empty or exceeds its configured bound.");
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maximumXmlBytes,
            MaxCharactersFromEntities = 0,
            IgnoreWhitespace = false
        };
        try
        {
            using var stream = new MemoryStream(content.ToArray(), writable: false);
            using var reader = XmlReader.Create(stream, settings);
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            document.Load(reader);
            return document;
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("XML is malformed or contains a prohibited construct.", exception);
        }
    }

    private static byte[] BuildSubmission(string serviceRoot, TransactionEngineSubmissionIdentity identity,
        string transactionId, TransactionEngineProtocolOptions options) => Encode($"""
        <?xml version="1.0" encoding="utf-8"?><GovTalkMessage xmlns="{GovTalkNamespace}"><EnvelopeVersion>2.0</EnvelopeVersion><Header><MessageDetails><Class>{Escape(options.MessageClass)}</Class><Qualifier>request</Qualifier><Function>submit</Function><TransactionID>{transactionId}</TransactionID><CorrelationID></CorrelationID><Transformation>XML</Transformation>{GatewayTest(identity)}</MessageDetails><SenderDetails>{Authentication(identity)}</SenderDetails></Header><GovTalkDetails><Keys><Key Type="UTR">{Escape(identity.Utr)}</Key></Keys><ChannelRouting><Channel><URI>{Escape(identity.VendorId)}</URI><Product>{Escape(identity.Product)}</Product><Version>{Escape(identity.ProductVersion)}</Version></Channel></ChannelRouting></GovTalkDetails><Body>{serviceRoot}</Body></GovTalkMessage>
        """);

    private static byte[] ControlMessage(string function, string correlationId,
        TransactionEngineProtocolOptions options)
    {
        ValidateOptions(options);
        ValidateCorrelationId(correlationId);
        return Encode($"""
            <?xml version="1.0" encoding="utf-8"?><GovTalkMessage xmlns="{GovTalkNamespace}"><EnvelopeVersion>2.0</EnvelopeVersion><Header><MessageDetails><Class>{Escape(options.MessageClass)}</Class><Qualifier>request</Qualifier><Function>{function}</Function><CorrelationID>{correlationId}</CorrelationID><Transformation>XML</Transformation></MessageDetails><SenderDetails></SenderDetails></Header><GovTalkDetails><Keys></Keys></GovTalkDetails><Body></Body></GovTalkMessage>
            """);
    }

    private static string Authentication(TransactionEngineSubmissionIdentity identity) =>
        $"<IDAuthentication><SenderID>{Escape(identity.SenderId)}</SenderID><Authentication><Method>clear</Method><Value>{Escape(identity.Password)}</Value></Authentication></IDAuthentication>";

    private static string GatewayTest(TransactionEngineSubmissionIdentity identity) =>
        identity.IsExternalTest ? "<GatewayTest>1</GatewayTest>" : "<GatewayTest>0</GatewayTest>";

    private static void VerifyOnlyIrmarkChanged(string before, string after, string expectedIrmark, int maximum)
    {
        var beforeDocument = LoadSecure(Encode(before), maximum);
        var afterDocument = LoadSecure(Encode(after), maximum);
        var beforeMark = beforeDocument.GetElementsByTagName("IRmark", CorporationTaxNamespace)
            .OfType<XmlElement>().Single();
        var afterMark = afterDocument.GetElementsByTagName("IRmark", CorporationTaxNamespace)
            .OfType<XmlElement>().Single();
        if (afterMark.InnerText != expectedIrmark)
            throw new InvalidOperationException("The calculated IRmark was not inserted into its reserved slot.");
        beforeMark.InnerText = expectedIrmark;
        if (beforeDocument.OuterXml != afterDocument.OuterXml)
            throw new InvalidOperationException("Materialisation changed CT service-root content outside IRmark.");
    }

    private static byte[] Encode(string value) => Utf8.GetBytes(value);
    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string StripXmlDeclaration(string value)
    {
        var declaration = Regex.Match(value, @"\A\s*<\?xml\s+[^?]*\?>",
            RegexOptions.CultureInvariant);
        return declaration.Success ? value[declaration.Length..] : value;
    }

    internal static void ValidateCorrelationId(string correlationId)
    {
        if (correlationId.Length != 32 || correlationId.Any(character =>
                !(character is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("A 32-character upper-case hexadecimal correlation ID is required.");
    }

    internal static void ValidateTransactionId(string transactionId)
    {
        if (transactionId.Length is < 1 or > 32 || transactionId.Any(character =>
                !(character is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException("A bounded upper-case hexadecimal transaction ID is required.");
    }

    private static void ValidateIdentity(TransactionEngineSubmissionIdentity identity)
    {
        foreach (var value in new[] { identity.SenderId, identity.Password, identity.Product, identity.ProductVersion })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl))
                throw new ArgumentException("Transaction Engine identity values must be bounded printable text.");
        if (identity.VendorId.Length != 4 || identity.VendorId.Any(character => !char.IsAsciiDigit(character)))
            throw new ArgumentException("A four-digit HMRC Vendor ID is required.", nameof(identity));
        if (identity.Utr.Length != 10 || identity.Utr.Any(character => !char.IsAsciiDigit(character)))
            throw new ArgumentException("A ten-digit Corporation Tax UTR is required.", nameof(identity));
    }

    private static void ValidateOptions(TransactionEngineProtocolOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.MessageClass) || options.MessageClass.Length > 64
            || options.SubmissionEndpoint.Scheme != Uri.UriSchemeHttps || !options.SubmissionEndpoint.IsDefaultPort
            || options.TrustedResponseHosts.Count == 0 || options.MaximumXmlBytes is < 1024 or > 32 * 1024 * 1024
            || options.RecoveryPollIntervalSeconds is < 1 or > 86_400)
            throw new ArgumentException("Transaction Engine protocol options are invalid.", nameof(options));
    }
}
