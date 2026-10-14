using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Xml;

namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

internal enum TransactionEngineResponseKind
{
    Acknowledgement,
    GatewayError,
    BusinessResponse,
    BusinessError,
    DeleteResponse,
    DataResponse
}

internal sealed record TransactionEngineError(
    string RaisedBy,
    string Number,
    string Type,
    string? Text,
    string? Location);

internal sealed record TransactionEngineStatusRecord(
    string CorrelationId,
    string Status,
    string? TransactionId);

internal sealed record TransactionEngineParsedResponse(
    TransactionEngineResponseKind Kind,
    string MessageClass,
    string Qualifier,
    string Function,
    string? CorrelationId,
    Uri? ResponseEndpoint,
    int? PollIntervalSeconds,
    string ResponseSha256,
    bool ContainsIrmarkReceipt,
    ImmutableArray<TransactionEngineError> Errors,
    ImmutableArray<TransactionEngineStatusRecord> StatusRecords);

internal static class TransactionEngineResponseParser
{
    internal static TransactionEngineStatusRecord MatchRecoveryRecord(
        TransactionEngineParsedResponse response, string transactionId)
    {
        TransactionEngineWire.ValidateTransactionId(transactionId);
        if (response.Kind != TransactionEngineResponseKind.DataResponse)
            throw new ArgumentException("A DATA_RESPONSE is required for correlation recovery.", nameof(response));
        var matches = response.StatusRecords.Where(record => record.TransactionId == transactionId).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new InvalidDataException("DATA_RESPONSE did not identify exactly one matching submission.");
    }

    internal static TransactionEngineParsedResponse Parse(ReadOnlySpan<byte> response,
        TransactionEngineProtocolOptions options)
    {
        var document = TransactionEngineWire.LoadSecure(response, options.MaximumXmlBytes);
        var root = document.DocumentElement;
        if (root?.LocalName != "GovTalkMessage" || root.NamespaceURI != TransactionEngineWire.GovTalkNamespace)
            throw new InvalidDataException("The response is not a GovTalk message.");

        var details = One(One(root, "Header"), "MessageDetails");
        var messageClass = Required(One(details, "Class"), 64);
        if (!string.Equals(messageClass, options.MessageClass, StringComparison.Ordinal))
            throw new InvalidDataException("The response class does not match the submitted document class.");
        var qualifier = Required(One(details, "Qualifier"), 32);
        var function = Required(One(details, "Function"), 32);
        var correlationId = Optional(ZeroOrOne(details, "CorrelationID"), 32);
        var errors = ParseErrors(root);
        var digest = Convert.ToHexString(SHA256.HashData(response));

        if (qualifier == "acknowledgement" && function == "submit")
        {
            TransactionEngineWire.ValidateCorrelationId(correlationId
                ?? throw new InvalidDataException("An acknowledgement has no correlation ID."));
            var responseEndpointElement = One(details, "ResponseEndPoint");
            var endpoint = TransactionEngineWire.ValidateResponseEndpoint(
                Required(responseEndpointElement, 2048), options);
            if (!int.TryParse(responseEndpointElement.GetAttribute("PollInterval"), out var pollInterval)
                || pollInterval is < 1 or > 86_400)
                throw new InvalidDataException("The acknowledgement poll interval is invalid.");
            if (errors.Length != 0 || HasBodyContent(root))
                throw new InvalidDataException("An acknowledgement contains forbidden error or body content.");
            return Result(TransactionEngineResponseKind.Acknowledgement, endpoint, pollInterval);
        }

        if (qualifier == "response" && function == "delete")
        {
            ValidateRequiredCorrelation(correlationId);
            return Result(TransactionEngineResponseKind.DeleteResponse);
        }

        if (qualifier == "response" && function == "list")
            return Result(TransactionEngineResponseKind.DataResponse, statusRecords: ParseStatuses(root));

        if (function == "submit" && qualifier == "response")
        {
            ValidateRequiredCorrelation(correlationId);
            return Result(TransactionEngineResponseKind.BusinessResponse,
                containsReceipt: root.GetElementsByTagName("IRmarkReceipt").Count > 0);
        }

        if (function == "submit" && qualifier == "error")
        {
            if (errors.Length == 0)
                throw new InvalidDataException("An error response contains no bounded GovTalk error.");
            var businessError = correlationId is not null && HasBodyContent(root);
            if (businessError) ValidateRequiredCorrelation(correlationId);
            return Result(businessError
                ? TransactionEngineResponseKind.BusinessError
                : TransactionEngineResponseKind.GatewayError);
        }

        throw new InvalidDataException("The GovTalk response does not represent a supported protocol message.");

        TransactionEngineParsedResponse Result(TransactionEngineResponseKind kind,
            Uri? endpoint = null, int? poll = null, bool containsReceipt = false,
            ImmutableArray<TransactionEngineStatusRecord>? statusRecords = null) => new(
                kind, messageClass, qualifier, function, correlationId, endpoint, poll, digest,
                containsReceipt, errors, statusRecords ?? []);
    }

    private static ImmutableArray<TransactionEngineError> ParseErrors(XmlElement root)
    {
        var errors = root.GetElementsByTagName("Error").OfType<XmlElement>()
            .Where(element => element.ParentNode is XmlElement parent
                && (parent.LocalName is "GovTalkErrors" or "ErrorResponse"))
            .Select(element => new TransactionEngineError(
                Child(element, "RaisedBy", 128) ?? string.Empty,
                Child(element, "Number", 64) ?? string.Empty,
                Child(element, "Type", 64) ?? string.Empty,
                Child(element, "Text", 2048),
                Child(element, "Location", 1024)))
            .Take(1001).ToArray();
        if (errors.Length > 1000)
            throw new InvalidDataException("The response contains too many error records.");
        if (errors.Any(error => string.IsNullOrWhiteSpace(error.Number)
                || string.IsNullOrWhiteSpace(error.Type)))
            throw new InvalidDataException("A GovTalk error is missing its required classification.");
        return errors.ToImmutableArray();
    }

    private static ImmutableArray<TransactionEngineStatusRecord> ParseStatuses(XmlElement root)
    {
        var records = root.GetElementsByTagName("StatusRecord").OfType<XmlElement>()
            .Take(1001).Select(element => new TransactionEngineStatusRecord(
                Child(element, "CorrelationID", 32)
                    ?? throw new InvalidDataException("A status record has no correlation ID."),
                Child(element, "Status", 64)
                    ?? throw new InvalidDataException("A status record has no status."),
                Child(element, "TransactionID", 32))).ToArray();
        if (records.Length > 1000)
            throw new InvalidDataException("The data response contains too many status records.");
        foreach (var record in records) TransactionEngineWire.ValidateCorrelationId(record.CorrelationId);
        return records.ToImmutableArray();
    }

    private static bool HasBodyContent(XmlElement root)
    {
        var body = One(root, "Body");
        return body.ChildNodes.OfType<XmlNode>().Any(node => node.NodeType == XmlNodeType.Element);
    }

    private static XmlElement One(XmlElement parent, string localName)
    {
        var matches = parent.ChildNodes.OfType<XmlElement>()
            .Where(element => element.LocalName == localName
                && element.NamespaceURI == TransactionEngineWire.GovTalkNamespace).ToArray();
        return matches.Length == 1 ? matches[0]
            : throw new InvalidDataException($"GovTalk requires exactly one {localName} element.");
    }

    private static XmlElement? ZeroOrOne(XmlElement parent, string localName)
    {
        var matches = parent.ChildNodes.OfType<XmlElement>()
            .Where(element => element.LocalName == localName
                && element.NamespaceURI == TransactionEngineWire.GovTalkNamespace).ToArray();
        return matches.Length switch
        {
            0 => null,
            1 => matches[0],
            _ => throw new InvalidDataException($"GovTalk contains duplicate {localName} elements.")
        };
    }

    private static string Required(XmlElement element, int maximum)
    {
        var value = element.InnerText.Trim();
        if (value.Length is 0 || value.Length > maximum || value.Any(char.IsControl))
            throw new InvalidDataException("A required response value is missing or invalid.");
        return value;
    }

    private static string? Optional(XmlElement? element, int maximum)
    {
        if (element is null || string.IsNullOrWhiteSpace(element.InnerText)) return null;
        return Required(element, maximum);
    }

    private static string? Child(XmlElement parent, string localName, int maximum)
    {
        var matches = parent.ChildNodes.OfType<XmlElement>()
            .Where(element => element.LocalName == localName).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"A response record duplicates {localName}.");
        return Optional(matches.SingleOrDefault(), maximum);
    }

    private static void ValidateRequiredCorrelation(string? correlationId) =>
        TransactionEngineWire.ValidateCorrelationId(correlationId
            ?? throw new InvalidDataException("The terminal response has no correlation ID."));
}
