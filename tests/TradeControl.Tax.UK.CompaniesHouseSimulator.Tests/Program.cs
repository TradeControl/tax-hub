using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;
using TradeControl.Tax.UK.Tools.CompaniesHouseSimulator;

var assertions = 0;
XNamespace GovTalk = CompaniesHouseEnvelopeSerializer.GovTalkNamespace;
XNamespace Form = CompaniesHouseEnvelopeSerializer.FormSubmissionNamespace;
XNamespace Status = CompaniesHouseEnvelopeSerializer.StatusNamespace;
var clock = new FixedSimulatorClock(new DateTimeOffset(2026, 10, 3, 12, 22, 0, TimeSpan.Zero));
var authentication = new SimulatorAuthentication("SIM-PRESENTER", "SIM-PRESENTER-AUTH", "SIM-COMPANY-AUTH");
var accountsBytes = Encoding.UTF8.GetBytes("<html xmlns='http://www.w3.org/1999/xhtml'><body>SIMULATED ACCOUNTS</body></html>");

var happy = Simulator();
var happyRequest = Filing("A00001", accountsBytes);
var happyAck = happy.Process(happyRequest, authentication, SimulatorScenarioCatalog.HappyAccept);
Assert(happyAck.Kind == SimulatorResponseKind.Acknowledgement
    && happyAck.EvidenceClass == SimulatorResponse.SimulatedEvidence,
    "The happy-path submission was not explicitly acknowledged as simulated.");
Assert(happyAck.AccountsSha256 == Hash(accountsBytes), "The exact accounts attachment digest was not preserved.");
var happyPending = happy.Process(SpecificPoll("A00001"), authentication);
Assert(Statuses(happyPending).Single() == "PENDING",
    "The first happy-path poll was not pending.");
var happyAccepted = happy.Process(SpecificPoll("A00001"), authentication);
Assert(Statuses(happyAccepted).Single() == "ACCEPT",
    "The happy-path submission did not reach acceptance.");

var conversation = CompaniesHouseFilingConversation.Start("A00001", Hash(happyRequest), Hash(accountsBytes))
    .ApplyAcknowledgement(new(null, clock.UtcNow, null, []), Evidence(happyAck, "A00001/acknowledgement"));
Assert(conversation.State == CompaniesHouseConversationState.Acknowledged && !conversation.IsTerminal,
    "The Application lifecycle confused gateway acknowledgement with filing acceptance.");
conversation = conversation.ApplyStatus(ParseStatuses(happyPending).Single(), false,
    Evidence(happyPending, "A00001/pending"));
conversation = conversation.ApplyStatus(ParseStatuses(happyAccepted).Single(), false,
    Evidence(happyAccepted, "A00001/accepted"));
Assert(conversation.State == CompaniesHouseConversationState.Accepted && conversation.IsTerminal
    && conversation.Evidence.All(item => item.Class == CompaniesHouseSimulationEvidence.EvidenceClass),
    "The simulator did not drive the Application conversation to an explicitly simulated terminal result.");
var happyEvidence = happy.GetSubmissionEvidence("A00001", authentication);
Assert(happyEvidence.RequestContent.AsSpan().SequenceEqual(happyRequest)
    && happyEvidence.AccountsContent.AsSpan().SequenceEqual(accountsBytes)
    && happyEvidence.RequestSha256 == Hash(happyRequest)
    && happyEvidence.AccountsSha256 == Hash(accountsBytes)
    && happyEvidence.EvidenceClass == SimulatorResponse.SimulatedEvidence,
    "The in-memory simulator evidence did not preserve the exact request and decoded accounts bytes.");

var rejected = Simulator();
var rejectedRequest = Filing("R00001", accountsBytes);
var rejectedAck = rejected.Process(rejectedRequest, authentication, SimulatorScenarioCatalog.BusinessReject);
rejected.Process(SpecificPoll("R00001"), authentication);
var rejection = rejected.Process(SpecificPoll("R00001"), authentication);
Assert(Statuses(rejection).Single() == "REJECT"
    && Xml(rejection).Descendants(Status + "RejectCode").Single().Value == "SIM-REJECT-001",
    "The business-rejection scenario lost its bounded rejection detail.");
var rejectedConversation = CompaniesHouseFilingConversation.Start("R00001",
        Hash(rejectedRequest), Hash(accountsBytes))
    .ApplyAcknowledgement(new(null, clock.UtcNow, null, []), Evidence(rejectedAck, "R00001/acknowledgement"))
    .ApplyStatus(ParseStatuses(rejection).Single(), false, Evidence(rejection, "R00001/rejected"));
Assert(rejectedConversation.State == CompaniesHouseConversationState.Rejected
    && rejectedConversation.Errors.Single().Code == "SIM-REJECT-001",
    "The Application lifecycle lost simulated business-rejection evidence.");
AssertRejected(() => rejectedConversation.ApplyStatus(ParseStatuses(rejection).Single(), false,
        Evidence(rejection, "R00001/repeated-terminal")),
    "A terminal Companies House result was allowed to change.");

var parked = Simulator();
parked.Process(Filing("P00001", accountsBytes), authentication, SimulatorScenarioCatalog.ParkedThenAccept);
Assert(Statuses(parked.Process(SpecificPoll("P00001"), authentication)).Single() == "PENDING", "Parked scenario skipped pending.");
Assert(Statuses(parked.Process(SpecificPoll("P00001"), authentication)).Single() == "PARKED", "Parked scenario skipped parked.");
Assert(Statuses(parked.Process(SpecificPoll("P00001"), authentication)).Single() == "ACCEPT", "Parked scenario did not accept.");

var failed = Simulator();
failed.Process(Filing("F00001", accountsBytes), authentication, SimulatorScenarioCatalog.InternalFailure);
Assert(Statuses(failed.Process(SpecificPoll("F00001"), authentication)).Single() == "INTERNAL_FAILURE",
    "The internal-failure scenario was not represented.");

var malformed = Simulator().Process("<not-xml"u8, authentication);
Assert(malformed.Kind == SimulatorResponseKind.GatewayError && malformed.ScenarioName == "malformed-envelope",
    "Malformed XML did not fail synchronously and explicitly.");
Assert(Simulator().Process(Filing("N00001", accountsBytes), authentication).Kind
    == SimulatorResponseKind.GatewayError, "An AA request without an explicit scenario was accepted.");
Assert(Simulator().Process(
        Filing("C00001", accountsBytes, "UnsupportedClass"), authentication, SimulatorScenarioCatalog.HappyAccept).Kind
    == SimulatorResponseKind.GatewayError, "An unsupported GovTalk class was accepted.");

var invalidAuthentication = Simulator().Process(
    Filing("X00001", accountsBytes), new("REAL-PRESENTER", "SIM-AUTH", "SIM-COMPANY"), SimulatorScenarioCatalog.HappyAccept);
Assert(invalidAuthentication.Kind == SimulatorResponseKind.GatewayError
    && invalidAuthentication.ScenarioName == "authentication-failure",
    "A non-synthetic authentication value was accepted.");
var gatewayRejected = CompaniesHouseFilingConversation.Start("X00001",
        invalidAuthentication.RequestSha256, Hash(accountsBytes))
    .ApplyAcknowledgement(new(null, clock.UtcNow, null,
        [new("SIM-AUTH", "The simulated gateway rejected authentication.")]),
        Evidence(invalidAuthentication, "X00001/gateway-rejected"));
Assert(gatewayRejected.State == CompaniesHouseConversationState.GatewayRejected
    && gatewayRejected.IsTerminal && gatewayRejected.Errors.Single().Code == "SIM-AUTH",
    "A synchronous gateway rejection was confused with an asynchronous business rejection.");

var duplicateSimulator = Simulator();
duplicateSimulator.Process(Filing("U00001", accountsBytes), authentication, SimulatorScenarioCatalog.HappyAccept);
Assert(duplicateSimulator.Process(Filing("U00001", accountsBytes), authentication, SimulatorScenarioCatalog.HappyAccept).Kind
    == SimulatorResponseKind.GatewayError, "A duplicate simulated submission overwrote active state.");
var wrongPresenter = new SimulatorAuthentication("SIM-OTHER", "SIM-OTHER-AUTH", "SIM-COMPANY-AUTH");
Assert(Statuses(duplicateSimulator.Process(SpecificPoll("U00001", "SIM-OTHER"), wrongPresenter)).Count == 0,
    "A specific poll disclosed another simulated presenter's submission.");
AssertRejected(() => duplicateSimulator.GetSubmissionEvidence("U00001", wrongPresenter),
    "The in-memory evidence API disclosed another simulated presenter's exact filing bytes.");

var general = Simulator();
var generalRequest = Filing("G00001", accountsBytes);
var generalAck = general.Process(generalRequest, authentication, SimulatorScenarioCatalog.GeneralPollRedelivery);
var firstGeneral = general.Process(PresenterPoll(), authentication);
var repeatedGeneral = general.Process(PresenterPoll(), authentication);
Assert(Statuses(firstGeneral).SequenceEqual(["ACCEPT"]) && Statuses(repeatedGeneral).SequenceEqual(["ACCEPT"]),
    "An unacknowledged general-poll status was not redelivered.");
var crossPresenterAck = general.Process(StatusAcknowledgement(), wrongPresenter);
Assert(crossPresenterAck.Kind == SimulatorResponseKind.GatewayError
    && Statuses(general.Process(PresenterPoll(), authentication)).SequenceEqual(["ACCEPT"]),
    "One simulated presenter acknowledged another presenter's outstanding status batch.");
var statusAck = general.Process(StatusAcknowledgement(), authentication);
Assert(statusAck.Kind == SimulatorResponseKind.StatusAcknowledgement,
    "The empty StatusAck was not acknowledged.");
Assert(Statuses(general.Process(PresenterPoll(), authentication)).Count == 0,
    "An acknowledged general-poll status was delivered again.");

var generalConversation = CompaniesHouseFilingConversation.Start("G00001",
        Hash(generalRequest), Hash(accountsBytes))
    .ApplyAcknowledgement(new(null, clock.UtcNow, null, []), Evidence(generalAck, "G00001/acknowledgement"))
    .ApplyStatus(ParseStatuses(firstGeneral).Single(), true, Evidence(firstGeneral, "G00001/general-status"));
Assert(generalConversation.StatusAcknowledgementRequired && !generalConversation.StatusAcknowledged,
    "A presenter-wide terminal result did not retain its conditional StatusAck requirement.");
generalConversation = generalConversation.AcknowledgeStatus(Evidence(statusAck, "G00001/status-ack"));
Assert(generalConversation.StatusAcknowledged && generalConversation.State == CompaniesHouseConversationState.Accepted,
    "StatusAck did not complete the simulated Application conversation evidence.");

var deterministicOne = Simulator().Process(
    Filing("D00001", accountsBytes), authentication, SimulatorScenarioCatalog.HappyAccept);
var deterministicTwo = Simulator().Process(
    Filing("D00001", accountsBytes), authentication, SimulatorScenarioCatalog.HappyAccept);
Assert(deterministicOne.Content.SequenceEqual(deterministicTwo.Content)
    && deterministicOne.ResponseSha256 == deterministicTwo.ResponseSha256,
    "Simulator output is not deterministic for fixed inputs, time and state.");

var controlledReferences = new CompaniesHouseDevelopmentSimulator(clock,
    new SequentialSimulatorReferenceGenerator("SIM-CONTROLLED", 41));
var controlledResponse = controlledReferences.Process(Filing("T00001", accountsBytes), authentication,
    SimulatorScenarioCatalog.HappyAccept);
Assert(Xml(controlledResponse).Descendants(GovTalk + "TransactionID").Single().Value == "SIM-CONTROLLED-000042",
    "The injected simulator reference generator did not control the response transaction ID.");

Assert(typeof(CompaniesHouseDevelopmentSimulator).Assembly.GetReferencedAssemblies()
        .All(reference => !reference.Name!.Contains("AspNetCore", StringComparison.Ordinal)
            && !reference.Name.Contains("Http", StringComparison.Ordinal)),
    "The Block A simulator contains a network or host dependency.");

Console.WriteLine($"Companies House simulator Block A tests passed ({assertions} assertions).");

void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

void AssertRejected(Action action, string message)
{
    assertions++;
    try { action(); }
    catch (ArgumentException) { return; }
    catch (InvalidOperationException) { return; }
    catch (KeyNotFoundException) { return; }
    throw new InvalidOperationException(message);
}

byte[] Filing(string submissionNumber, byte[] accounts, string messageClass = "Accounts") => Serialize(new XDocument(
    new XDeclaration("1.0", "utf-8", null), new XElement(GovTalk + "GovTalkMessage",
        new XElement(GovTalk + "EnvelopeVersion", "1.0"),
        new XElement(GovTalk + "Header", new XElement(GovTalk + "MessageDetails",
            new XElement(GovTalk + "Class", messageClass), new XElement(GovTalk + "Qualifier", "request"),
            new XElement(GovTalk + "Function", "submit"))),
        new XElement(GovTalk + "GovTalkDetails"),
        new XElement(GovTalk + "Body", new XElement(Form + "FormSubmission",
            new XElement(Form + "FormHeader", new XElement(Form + "CompanyNumber", "01234567"),
                new XElement(Form + "CompanyType", "EW"), new XElement(Form + "CompanyName", "SIMULATOR LIMITED"),
                new XElement(Form + "PackageReference", "SIM-PACKAGE"), new XElement(Form + "Language", "EN"),
                new XElement(Form + "FormIdentifier", "Accounts"), new XElement(Form + "SubmissionNumber", submissionNumber)),
            new XElement(Form + "DateSigned", "2026-10-03"), new XElement(Form + "Form"),
            new XElement(Form + "Document", new XElement(Form + "Data", Convert.ToBase64String(accounts)),
                new XElement(Form + "Filename", "sim-accounts.xhtml"),
                new XElement(Form + "ContentType", "application/xml"), new XElement(Form + "Category", "ACCOUNTS")))))));

byte[] SpecificPoll(string submissionNumber, string presenterId = "SIM-PRESENTER") => new CompaniesHouseEnvelopeSerializer()
    .SerializeStatusRequest(new(presenterId, CompaniesHouseStatusQueryScope.Submission, submissionNumber)).Content;
byte[] PresenterPoll() => new CompaniesHouseEnvelopeSerializer()
    .SerializeStatusRequest(new("SIM-PRESENTER", CompaniesHouseStatusQueryScope.Presenter)).Content;
byte[] StatusAcknowledgement() => new CompaniesHouseEnvelopeSerializer().SerializeStatusAcknowledgement().Content;
XDocument Xml(SimulatorResponse response) => XDocument.Parse(Encoding.UTF8.GetString(response.Content.AsSpan()));
IReadOnlyList<string> Statuses(SimulatorResponse response) =>
    Xml(response).Descendants(Status + "StatusCode").Select(element => element.Value).ToArray();
IReadOnlyList<CompaniesHouseSubmissionStatus> ParseStatuses(SimulatorResponse response) =>
    new CompaniesHouseEnvelopeSerializer().ParseSubmissionStatuses(response.Content.AsSpan());
CompaniesHouseSimulationEvidence Evidence(SimulatorResponse response, string safeReference) =>
    new(safeReference, response.RequestSha256, response.ResponseSha256);
CompaniesHouseDevelopmentSimulator Simulator() => new(clock, new SequentialSimulatorReferenceGenerator());
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

byte[] Serialize(XDocument document)
{
    using var stream = new MemoryStream();
    using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
           { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None }))
        document.Save(writer);
    return stream.ToArray();
}
