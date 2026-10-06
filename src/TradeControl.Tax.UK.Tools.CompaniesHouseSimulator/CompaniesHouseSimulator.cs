using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TradeControl.Tax.UK.Company.ContractInfrastructure;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

namespace TradeControl.Tax.UK.Tools.CompaniesHouseSimulator;

public sealed record SimulatorAuthentication(string PresenterId, string PresenterAuthentication,
    string CompanyAuthentication)
{
    public void Validate()
    {
        RequireSynthetic(PresenterId, nameof(PresenterId));
        RequireSynthetic(PresenterAuthentication, nameof(PresenterAuthentication));
        RequireSynthetic(CompanyAuthentication, nameof(CompanyAuthentication));
    }

    private static void RequireSynthetic(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("SIM-", StringComparison.Ordinal))
            throw new ArgumentException("Simulator authentication values must use the SIM- prefix.", name);
    }
}

public interface ISimulatorClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class FixedSimulatorClock(DateTimeOffset utcNow) : ISimulatorClock
{
    public DateTimeOffset UtcNow { get; } = utcNow;
}

public interface ISimulatorReferenceGenerator
{
    string NextTransactionId();
}

public sealed class SequentialSimulatorReferenceGenerator : ISimulatorReferenceGenerator
{
    private readonly string _prefix;
    private int _sequence;

    public SequentialSimulatorReferenceGenerator(string prefix = "SIM", int initialSequence = 0)
    {
        if (string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith("SIM", StringComparison.Ordinal)
            || prefix.Any(char.IsControl) || initialSequence < 0)
            throw new ArgumentException("A synthetic SIM reference prefix and non-negative sequence are required.");
        _prefix = prefix;
        _sequence = initialSequence;
    }

    public string NextTransactionId() => $"{_prefix}-{++_sequence:000000}";
}

public sealed record SimulatorScenario
{
    public SimulatorScenario(string name, IReadOnlyList<CompaniesHouseSubmissionStatusCode> statuses,
        IReadOnlyList<CompaniesHouseError> rejections)
    {
        if (string.IsNullOrWhiteSpace(name) || statuses.Count == 0)
            throw new ArgumentException("A simulator scenario requires a name and at least one status.");
        Name = name;
        Statuses = statuses.ToImmutableArray();
        Rejections = rejections.ToImmutableArray();
    }

    public string Name { get; }
    public ImmutableArray<CompaniesHouseSubmissionStatusCode> Statuses { get; }
    public ImmutableArray<CompaniesHouseError> Rejections { get; }
}

public static class SimulatorScenarioCatalog
{
    public static SimulatorScenario HappyAccept { get; } = new("happy-accept",
        [CompaniesHouseSubmissionStatusCode.Pending, CompaniesHouseSubmissionStatusCode.Accept], []);
    public static SimulatorScenario BusinessReject { get; } = new("business-reject",
        [CompaniesHouseSubmissionStatusCode.Pending, CompaniesHouseSubmissionStatusCode.Reject],
        [new("SIM-REJECT-001", "Simulated business rejection.", "1")]);
    public static SimulatorScenario ParkedThenAccept { get; } = new("parked-then-accept",
        [CompaniesHouseSubmissionStatusCode.Pending, CompaniesHouseSubmissionStatusCode.Parked,
            CompaniesHouseSubmissionStatusCode.Accept], []);
    public static SimulatorScenario InternalFailure { get; } = new("internal-failure",
        [CompaniesHouseSubmissionStatusCode.InternalFailure], []);
    public static SimulatorScenario GeneralPollRedelivery { get; } = new("general-poll-redelivery",
        [CompaniesHouseSubmissionStatusCode.Accept], []);

    public static IReadOnlyDictionary<string, SimulatorScenario> All { get; } = new Dictionary<string, SimulatorScenario>(StringComparer.Ordinal)
    {
        [HappyAccept.Name] = HappyAccept,
        [BusinessReject.Name] = BusinessReject,
        [ParkedThenAccept.Name] = ParkedThenAccept,
        [InternalFailure.Name] = InternalFailure,
        [GeneralPollRedelivery.Name] = GeneralPollRedelivery
    };
}

public enum SimulatorResponseKind { Acknowledgement, SubmissionStatus, StatusAcknowledgement, GatewayError }

public sealed record SimulatorResponse(SimulatorResponseKind Kind, ImmutableArray<byte> Content,
    string RequestSha256, string ResponseSha256, string EvidenceClass, string ScenarioName,
    string? SubmissionNumber = null, string? AccountsSha256 = null)
{
    public const string SimulatedEvidence = "SIMULATED — NOT FILED";
}

public sealed record SimulatorSubmissionEvidence(string SubmissionNumber, string PresenterId,
    string ScenarioName, ImmutableArray<byte> RequestContent, string RequestSha256,
    ImmutableArray<byte> AccountsContent, string AccountsSha256,
    CompaniesHouseSubmissionStatusCode CurrentStatus, string EvidenceClass);

public sealed class CompaniesHouseDevelopmentSimulator
{
    private static readonly XNamespace GovTalk = CompaniesHouseEnvelopeSerializer.GovTalkNamespace;
    private static readonly XNamespace Form = CompaniesHouseEnvelopeSerializer.FormSubmissionNamespace;
    private static readonly XNamespace Status = CompaniesHouseEnvelopeSerializer.StatusNamespace;
    private readonly ISimulatorClock _clock;
    private readonly ISimulatorReferenceGenerator _referenceGenerator;
    private readonly Dictionary<string, StoredSubmission> _submissions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acknowledgedGeneralStatuses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]> _lastGeneralBatches = new(StringComparer.Ordinal);

    public CompaniesHouseDevelopmentSimulator(ISimulatorClock clock,
        ISimulatorReferenceGenerator referenceGenerator)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _referenceGenerator = referenceGenerator ?? throw new ArgumentNullException(nameof(referenceGenerator));
    }

    public SimulatorSubmissionEvidence GetSubmissionEvidence(string submissionNumber,
        SimulatorAuthentication authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        authentication.Validate();
        if (!_submissions.TryGetValue(submissionNumber, out var item)
            || !string.Equals(item.PresenterId, authentication.PresenterId, StringComparison.Ordinal))
            throw new KeyNotFoundException("No simulated submission evidence is available for that presenter.");
        return new(item.SubmissionNumber, item.PresenterId, item.Scenario.Name,
            item.RequestContent, item.RequestSha256, item.AccountsContent, item.AccountsSha256,
            item.CurrentStatus, SimulatorResponse.SimulatedEvidence);
    }

    public SimulatorResponse Process(ReadOnlySpan<byte> requestXml, SimulatorAuthentication authentication,
        SimulatorScenario? scenario = null)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        var request = requestXml.ToArray();
        var requestHash = Hash(request);
        try
        {
            authentication.Validate();
        }
        catch (ArgumentException exception)
        {
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-AUTH", exception.Message), "authentication-failure");
        }

        XDocument document;
        try
        {
            using var stream = new MemoryStream(request, writable: false);
            document = SecureXml.Parse(stream);
        }
        catch (XmlException exception)
        {
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-XML", exception.Message), scenario?.Name ?? "malformed-envelope");
        }

        var root = document.Root;
        var messageDetails = root?.Element(GovTalk + "Header")?.Element(GovTalk + "MessageDetails");
        var messageClass = messageDetails?.Element(GovTalk + "Class")?.Value;
        var function = messageDetails?.Element(GovTalk + "Function")?.Value;
        var body = root?.Element(GovTalk + "Body");
        if (root?.Name != GovTalk + "GovTalkMessage" || body is null || string.IsNullOrWhiteSpace(messageClass)
            || messageDetails?.Element(GovTalk + "Qualifier")?.Value != "request"
            || messageClass == "Accounts" && function != "submit"
            || messageClass is "GetSubmissionStatus" or "StatusAck" && function is not null)
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-ENVELOPE", "The simulated request is not a supported GovTalk envelope."),
                scenario?.Name ?? "invalid-envelope");

        return messageClass switch
        {
            "Accounts" => Submit(request, requestHash, body, authentication, scenario),
            "GetSubmissionStatus" => Poll(requestHash, body, authentication),
            "StatusAck" => AcknowledgeStatus(requestHash, body, authentication),
            _ => Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-CLASS", $"Unsupported simulated GovTalk Class '{messageClass}'."),
                scenario?.Name ?? "unsupported-class")
        };
    }

    private SimulatorResponse Submit(byte[] request, string requestHash, XElement body,
        SimulatorAuthentication authentication, SimulatorScenario? scenario)
    {
        if (scenario is null)
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-SCENARIO", "An AA simulator request requires an explicit scenario."), "missing-scenario");
        var submission = body.Element(Form + "FormSubmission");
        var header = submission?.Element(Form + "FormHeader");
        var document = submission?.Element(Form + "Document");
        var number = header?.Element(Form + "SubmissionNumber")?.Value;
        var company = header?.Element(Form + "CompanyNumber")?.Value;
        var packageReference = header?.Element(Form + "PackageReference")?.Value;
        var data = document?.Element(Form + "Data")?.Value;
        if (submission is null || header is null || document is null || number?.Length != 6
            || string.IsNullOrWhiteSpace(company) || string.IsNullOrWhiteSpace(packageReference)
            || header.Element(Form + "FormIdentifier")?.Value != "Accounts"
            || submission.Element(Form + "DateSigned") is null
            || body.Elements(Form + "FormSubmission").Count() != 1
            || submission.Elements(Form + "Document").Count() != 1
            || document.Element(Form + "Filename")?.Value is not { Length: <= 32 }
            || string.IsNullOrWhiteSpace(data)
            || document.Element(Form + "ContentType")?.Value != "application/xml"
            || document.Element(Form + "Category")?.Value != "ACCOUNTS")
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-FORM", "The simulated FormSubmission does not meet the supported published shape."),
                scenario.Name, number);

        if (_submissions.ContainsKey(number))
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-DUPLICATE", "The simulated submission number is already active."),
                scenario.Name, number);

        byte[] accounts;
        try { accounts = Convert.FromBase64String(data); }
        catch (FormatException)
        {
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-BASE64", "The simulated accounts attachment is not valid base64."), scenario.Name, number);
        }

        _submissions[number] = new(number, company, authentication.PresenterId, request,
            accounts, scenario, 0);
        var acknowledgement = Envelope("Accounts", "acknowledgement", null);
        return Response(SimulatorResponseKind.Acknowledgement, requestHash, acknowledgement,
            scenario.Name, number, Hash(accounts));
    }

    private SimulatorResponse Poll(string requestHash, XElement body, SimulatorAuthentication authentication)
    {
        var request = body.Element(Status + "GetSubmissionStatus");
        if (request is null || request.Element(Status + "PresenterID")?.Value != authentication.PresenterId)
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-STATUS", "The simulated status request is invalid."), "status-request");
        var requestedNumber = request.Element(Status + "SubmissionNumber")?.Value;
        IReadOnlyList<StoredSubmission> matches;
        if (!string.IsNullOrWhiteSpace(requestedNumber))
        {
            matches = _submissions.TryGetValue(requestedNumber, out var item)
                && item.PresenterId == authentication.PresenterId ? [item] : [];
        }
        else
        {
            matches = _submissions.Values.Where(item => item.PresenterId == authentication.PresenterId
                    && IsTerminal(item.CurrentStatus) && !_acknowledgedGeneralStatuses.Contains(item.SubmissionNumber))
                .OrderBy(item => item.SubmissionNumber, StringComparer.Ordinal).ToArray();
            _lastGeneralBatches[authentication.PresenterId] =
                matches.Select(item => item.SubmissionNumber).ToArray();
        }

        var statusBody = new XElement(Status + "SubmissionStatus", matches.Select(StatusElement));
        foreach (var item in matches)
            if (!string.IsNullOrWhiteSpace(requestedNumber)) item.Advance();
        return Response(SimulatorResponseKind.SubmissionStatus, requestHash,
            Envelope("GetSubmissionStatus", "response", statusBody), "status-poll", requestedNumber);
    }

    private SimulatorResponse AcknowledgeStatus(string requestHash, XElement body,
        SimulatorAuthentication authentication)
    {
        var acknowledgement = body.Element(Status + "StatusAck");
        if (acknowledgement is null || acknowledgement.HasElements || !string.IsNullOrWhiteSpace(acknowledgement.Value))
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-STATUS-ACK", "StatusAck must contain an empty StatusAck body."), "status-ack");
        if (!_lastGeneralBatches.Remove(authentication.PresenterId, out var batch) || batch.Length == 0)
            return Response(SimulatorResponseKind.GatewayError, requestHash,
                ErrorEnvelope("SIM-STATUS-ACK", "No presenter-wide simulated status batch is awaiting acknowledgement."),
                "status-ack");
        foreach (var number in batch) _acknowledgedGeneralStatuses.Add(number);
        return Response(SimulatorResponseKind.StatusAcknowledgement, requestHash,
            Envelope("StatusAck", "acknowledgement", null), "status-ack");
    }

    private static XElement StatusElement(StoredSubmission item) =>
        new(Status + "Status",
            new XElement(Status + "SubmissionNumber", item.SubmissionNumber),
            new XElement(Status + "StatusCode", StatusCode(item.CurrentStatus)),
            new XElement(Status + "CompanyNumber", item.CompanyNumber),
            item.CurrentStatus == CompaniesHouseSubmissionStatusCode.Reject
                ? new XElement(Status + "Rejections", item.Scenario.Rejections.Select(error =>
                    new XElement(Status + "Reject", new XElement(Status + "RejectCode", error.Code),
                        new XElement(Status + "Description", error.Message),
                        error.Location is null ? null : new XElement(Status + "InstanceNumber", error.Location))))
                : null);

    private SimulatorResponse Response(SimulatorResponseKind kind, string requestHash, XDocument document,
        string scenario, string? submissionNumber = null, string? accountsHash = null)
    {
        var bytes = Serialize(document);
        return new(kind, ImmutableArray.Create(bytes), requestHash, Hash(bytes), SimulatorResponse.SimulatedEvidence,
            scenario, submissionNumber, accountsHash);
    }

    private XDocument ErrorEnvelope(string code, string message) => Envelope("Error", "response",
        new XElement(Status + "SimulatorError", new XElement(Status + "Code", code),
            new XElement(Status + "Message", message),
            new XElement(Status + "Evidence", SimulatorResponse.SimulatedEvidence)));

    private XDocument Envelope(string messageClass, string qualifier, XElement? body) => new(
        new XDeclaration("1.0", "utf-8", null), new XElement(GovTalk + "GovTalkMessage",
            new XElement(GovTalk + "EnvelopeVersion", "1.0"),
            new XElement(GovTalk + "Header", new XElement(GovTalk + "MessageDetails",
                new XElement(GovTalk + "Class", messageClass),
                new XElement(GovTalk + "Qualifier", qualifier),
                new XElement(GovTalk + "Function", "submit"),
                new XElement(GovTalk + "TransactionID", NextTransactionId()),
                new XElement(GovTalk + "GatewayTimestamp", _clock.UtcNow.ToString("O")))),
            new XElement(GovTalk + "GovTalkDetails"), new XElement(GovTalk + "Body", body)));

    private static byte[] Serialize(XDocument document)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
               { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None }))
            document.Save(writer);
        return stream.ToArray();
    }

    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexString(SHA256.HashData(value));
    private string NextTransactionId()
    {
        var value = _referenceGenerator.NextTransactionId();
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("SIM", StringComparison.Ordinal)
            || value.Any(char.IsControl))
            throw new InvalidOperationException("The simulator reference generator returned a non-synthetic transaction ID.");
        return value;
    }
    private static bool IsTerminal(CompaniesHouseSubmissionStatusCode status) => status is
        CompaniesHouseSubmissionStatusCode.Accept or CompaniesHouseSubmissionStatusCode.Reject
        or CompaniesHouseSubmissionStatusCode.InternalFailure;
    private static string StatusCode(CompaniesHouseSubmissionStatusCode status) => status switch
    {
        CompaniesHouseSubmissionStatusCode.Accept => "ACCEPT",
        CompaniesHouseSubmissionStatusCode.Reject => "REJECT",
        CompaniesHouseSubmissionStatusCode.Pending => "PENDING",
        CompaniesHouseSubmissionStatusCode.Parked => "PARKED",
        CompaniesHouseSubmissionStatusCode.InternalFailure => "INTERNAL_FAILURE",
        _ => throw new InvalidOperationException()
    };

    private sealed class StoredSubmission(string submissionNumber, string companyNumber, string presenterId,
        byte[] requestBytes, byte[] accountsBytes, SimulatorScenario scenario, int statusIndex)
    {
        public string SubmissionNumber { get; } = submissionNumber;
        public string CompanyNumber { get; } = companyNumber;
        public string PresenterId { get; } = presenterId;
        public ImmutableArray<byte> RequestContent { get; } = ImmutableArray.Create(requestBytes);
        public string RequestSha256 { get; } = Hash(requestBytes);
        public ImmutableArray<byte> AccountsContent { get; } = ImmutableArray.Create(accountsBytes);
        public string AccountsSha256 { get; } = Hash(accountsBytes);
        public SimulatorScenario Scenario { get; } = scenario;
        public int StatusIndex { get; private set; } = statusIndex;
        public CompaniesHouseSubmissionStatusCode CurrentStatus => Scenario.Statuses[StatusIndex];
        public void Advance()
        {
            if (StatusIndex < Scenario.Statuses.Length - 1) StatusIndex++;
        }
    }
}
