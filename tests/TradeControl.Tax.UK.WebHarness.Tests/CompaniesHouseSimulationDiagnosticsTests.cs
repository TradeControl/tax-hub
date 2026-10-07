using System.Text;
using System.Text.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;
using TradeControl.Tax.UK.Tools.CompaniesHouseSimulator;
using TradeControl.Tax.UK.WebHarness.Controllers;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

internal static class CompaniesHouseSimulationDiagnosticsTests
{
    public static async Task<int> RunAsync()
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var package = Package("S00001");
        var eventMessage = CompaniesHouseSubmissionEventLog.BuildMessage(
            package.Documents.Single().Artifact.Content.ToArray(),
            package.Documents.Single().Artifact.Sha256, "S00001", "2026100600000001",
            "Acknowledgement", new string('A', 64), new string('B', 64), []);
        Assert(eventMessage.Contains("DATABASE-DERIVED ACCOUNTS", StringComparison.Ordinal)
            && eventMessage.Contains("transport credentials excluded", StringComparison.OrdinalIgnoreCase)
            && !eventMessage.Contains("TESTAUTHENTICATION", StringComparison.Ordinal),
            "The application event message did not preserve the exact safe accounts payload.");
        var runner = new PreparedRunner(package);
        var diagnostics = new CompaniesHouseSimulationDiagnostics(runner,
            new InProcessCompaniesHouseDiagnosticGatewayFactory());
        var payload = new CompaniesHouseAccountsPayload
        {
            AsOfDate = new(2026, 10, 3),
            Filing = new() { SubmissionNumber = "S00001", TransactionId = "2026100600000001" },
            Simulation = new() { Scenario = "happy-accept" }
        };
        Assert(typeof(CompaniesHouseAccountsReviewPayload).GetProperties()
                .All(property => !property.Name.Contains("Tax", StringComparison.OrdinalIgnoreCase)),
            "The Companies House diagnostic contract exposes Corporation Tax review inputs.");
        payload.Review.Extra = new Dictionary<string, JsonElement>
        {
            ["taxOnProfit"] = JsonDocument.Parse("0").RootElement.Clone()
        };
        var payloadValidation = new CompaniesHouseAccountsPayloadValidator(new CompanyAccountsPayloadValidator())
            .Validate(payload);
        Assert(payloadValidation.Errors.ContainsKey("review.taxOnProfit"),
            "An obsolete Corporation Tax field was silently accepted by the Companies House endpoint.");
        payload.Review.Extra = null;
        var outcome = await diagnostics.ExecuteAsync(payload);
        Assert(ReferenceEquals(runner.Received, payload),
            "The WebHarness simulation did not pass the request through its preparation boundary.");
        Assert(outcome.TaxHub.EvidenceClass == SimulatorResponse.SimulatedEvidence
            && outcome.TaxHub.SubmissionNumber == "S00001" && outcome.TaxHub.Scenario == "happy-accept",
            "The WebHarness result is not unmistakably identified as the selected simulation.");
        Assert(outcome.TaxHub.States.SequenceEqual(["Acknowledged", "Pending", "Accepted"])
            && outcome.TaxHub.FinalState == "Accepted" && outcome.TaxHub.IsTerminal,
            "The WebHarness did not drive the in-process simulator through acknowledgement, polling and acceptance.");
        Assert(outcome.TaxHub.TransmissionSha256 == package.Transmission.Sha256
            && outcome.TaxHub.AccountsSha256 == package.Documents.Single().Artifact.Sha256,
            "The WebHarness simulation changed the prepared transmission or accounts identity.");
        Assert(!outcome.TaxHub.StatusAcknowledgementRequired && !outcome.TaxHub.StatusAcknowledged,
            "A specific-submission poll incorrectly required StatusAck.");
        Assert(outcome.CompaniesHouse.EvidenceClass == SimulatorResponse.SimulatedEvidence
            && outcome.CompaniesHouse.Exchanges.Select(exchange => exchange.Operation)
                .SequenceEqual(["FormSubmission", "GetSubmissionStatus", "GetSubmissionStatus"]),
            "The Companies House protocol transcript does not identify the complete simulated exchange.");
        Assert(outcome.CompaniesHouse.Exchanges[0].RequestXml.Contains("<FormSubmission", StringComparison.Ordinal)
            && outcome.CompaniesHouse.Exchanges[0].ResponseXml.Contains("<Qualifier>acknowledgement</Qualifier>", StringComparison.Ordinal)
            && outcome.CompaniesHouse.Exchanges[0].ResponseXml.Contains("<GatewayTimestamp>", StringComparison.Ordinal)
            && XDocument.Parse(outcome.CompaniesHouse.Exchanges[0].ResponseXml).Descendants()
                .Single(element => element.Name.LocalName == "Body").IsEmpty
            && outcome.CompaniesHouse.Exchanges[1].RequestXml.Contains("<GetSubmissionStatus", StringComparison.Ordinal)
            && outcome.CompaniesHouse.Exchanges[1].ResponseXml.Contains("<StatusCode>PENDING</StatusCode>", StringComparison.Ordinal)
            && outcome.CompaniesHouse.Exchanges[2].ResponseXml.Contains("<StatusCode>ACCEPT</StatusCode>", StringComparison.Ordinal),
            "The Companies House section does not expose the actual GovTalk/XML request and response bodies.");
        var simulateAction = typeof(CompaniesHouseAccountsController).GetMethod(
            nameof(CompaniesHouseAccountsController.Simulate));
        Assert(simulateAction?.GetCustomAttribute<HttpPostAttribute>()?.Template == "simulate",
            "The Companies House WebHarness simulation endpoint is not exposed on its explicit route.");
        var safeProperties = typeof(CompaniesHouseSimulationOutcome).GetProperties()
            .Concat(typeof(CompaniesHouseSimulationSummary).GetProperties())
            .Concat(typeof(CompaniesHouseProtocolTranscript).GetProperties())
            .Concat(typeof(CompaniesHouseProtocolExchange).GetProperties())
            .Select(property => property.Name).ToArray();
        Assert(new[] { "SqlConnection", "Credential", "Authentication" }
            .All(forbidden => safeProperties.All(name => !name.Contains(forbidden, StringComparison.OrdinalIgnoreCase))),
            "The Companies House simulation response exposes connection or credential material.");
        Assert(!new DisabledCompaniesHouseSimulationDiagnostics().Enabled,
            "The non-Development Companies House simulator composition is enabled.");
        var disabledOfficialGateway = new SendDisabledCompaniesHouseOfficialTestGatewayFactory().Create(payload);
        Assert(disabledOfficialGateway.EvidenceClass.Contains("SEND DISABLED", StringComparison.Ordinal),
            "The official test gateway placeholder is not visibly send-disabled.");
        try
        {
            await disabledOfficialGateway.ExchangeAsync(new(CompaniesHouseGatewayOperation.FormSubmission,
                package.Transmission.Content));
            throw new InvalidOperationException("The official test gateway placeholder allowed an external exchange.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("sending remains disabled",
                   StringComparison.Ordinal))
        {
            assertions++;
        }

        var secretFile = Path.Combine(Path.GetTempPath(), $"tax-hub-ch-preflight-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(secretFile, JsonSerializer.Serialize(new
            {
                testPresenterId = "TESTPRESENTER",
                testPresenterAuthentication = "TESTAUTHENTICATION",
                testFlag = "1",
                testPackageReference = "1234",
                companyAuthentication = "ABC123"
            }));
            var testSecrets = new JsonFileSecretProvider(secretFile,
                CompaniesHouseSecretReferences.JsonProperties
                    .Where(item => item.Key != CompaniesHouseSecretReferences.CompanyAuthentication)
                    .ToDictionary());
            var companySecrets = new JsonFileSecretProvider(secretFile,
                new Dictionary<SecretReference, string>
                {
                    [CompaniesHouseSecretReferences.CompanyAuthentication] = "companyAuthentication"
                });
            var preflight = new CompaniesHouseOfficialTestPreflight(runner, testSecrets, companySecrets);
            var preflightOutcome = await preflight.ExecuteAsync(payload);
            Assert(preflightOutcome.DatabasePackagePrepared
                && preflightOutcome.CredentialedEnvelopeMaterialized
                && !preflightOutcome.ExternalSendEnabled,
                "The official-test preflight did not prepare and materialise the package while keeping send disabled.");
            Assert(preflightOutcome.PreparationSource.StartsWith("Trade Control database", StringComparison.Ordinal)
                && preflightOutcome.IntendedEndpoint == CompaniesHouseGatewayEndpoints.OfficialTest.ToString(),
                "The official-test preflight does not make its data source and intended endpoint explicit.");
            Assert(preflightOutcome.ProtectedConfiguration.TestPresenterConfigurationAvailable
                && preflightOutcome.ProtectedConfiguration.CompanyAuthenticationAvailable
                && preflightOutcome.Digests.WireSha256 is not null,
                "The official-test preflight did not report the protected configuration and wire digest safely.");
            Assert(preflightOutcome.BlockingReasons.Contains("CH-EXTERNAL-SEND-DISABLED")
                && preflightOutcome.BlockingReasons.Contains("CH-DEVELOPER-TEST-EVIDENCE-PENDING"),
                "The official-test preflight did not preserve its fail-closed gates.");
            var preflightJson = JsonSerializer.Serialize(preflightOutcome);
            Assert(!preflightJson.Contains("TESTPRESENTER", StringComparison.Ordinal)
                && !preflightJson.Contains("TESTAUTHENTICATION", StringComparison.Ordinal)
                && !preflightJson.Contains("ABC123", StringComparison.Ordinal)
                && !preflightJson.Contains("GovTalkMessage", StringComparison.Ordinal),
                "The official-test preflight response exposed a protected value or credentialed XML.");
            var preflightAction = typeof(CompaniesHouseAccountsController).GetMethod(
                nameof(CompaniesHouseAccountsController.OfficialTestPreflight));
            Assert(preflightAction?.GetCustomAttribute<HttpPostAttribute>()?.Template == "official-test-preflight",
                "The official-test preflight endpoint is not exposed on its explicit route.");
            Assert(!new DisabledCompaniesHouseOfficialTestPreflight().Enabled,
                "The non-Development official-test preflight composition is enabled.");

            var routed = new RoutedSecretProvider(new Dictionary<SecretReference, ISecretProvider>
            {
                [CompaniesHouseSecretReferences.TestPresenterId] = testSecrets,
                [CompaniesHouseSecretReferences.TestPresenterAuthentication] = testSecrets,
                [CompaniesHouseSecretReferences.TestFlag] = testSecrets,
                [CompaniesHouseSecretReferences.TestPackageReference] = testSecrets,
                [CompaniesHouseSecretReferences.CompanyAuthentication] = companySecrets
            });
            var evidenceRoot = Path.Combine(Path.GetTempPath(), $"tax-hub-ch-evidence-{Guid.NewGuid():N}");
            try
            {
                payload.Filing.AuthorizeOfficialTestSubmission = true;
                var gateway = new AcknowledgingGateway();
                var submissionEventLog = new RecordingSubmissionEventLog();
                var officialSubmission = new CompaniesHouseOfficialTestSubmission(
                    runner, routed, gateway, submissionEventLog, evidenceRoot);
                var officialOutcome = await officialSubmission.ExecuteOnceAsync(payload);
                Assert(officialOutcome.ResponseKind == CompaniesHouseGatewayResponseKind.Acknowledgement.ToString()
                    && officialOutcome.ExternalRequestCount == 1 && !officialOutcome.RetryPermitted
                    && !officialOutcome.TerminalFilingDecisionReceived && gateway.ExchangeCount == 1
                    && officialOutcome.EventLogRecorded && officialOutcome.EventLogCode == "TEST00001"
                    && submissionEventLog.WriteCount == 1 && !submissionEventLog.IsError
                    && submissionEventLog.AccountsPayload.Contains("DATABASE-DERIVED ACCOUNTS",
                        StringComparison.Ordinal),
                    "The controlled official-test boundary did not record exactly one non-terminal send.");
                var repeatedOutcome = await officialSubmission.ExecuteOnceAsync(payload);
                Assert(ReferenceEquals(officialOutcome, repeatedOutcome) && gateway.ExchangeCount == 1,
                    "Repeating a completed one-shot request caused another gateway exchange.");
                Assert(Directory.GetFiles(evidenceRoot, "request.protected.xml", SearchOption.AllDirectories).Length == 1
                    && Directory.GetFiles(evidenceRoot, "response.xml", SearchOption.AllDirectories).Length == 1
                    && Directory.GetFiles(evidenceRoot, "pre-send.json", SearchOption.AllDirectories).Length == 1
                    && Directory.GetFiles(evidenceRoot, "outcome.json", SearchOption.AllDirectories).Length == 1,
                    "The one-shot boundary did not retain its protected pre-send and response evidence.");
                var submitAction = typeof(CompaniesHouseAccountsController).GetMethod(
                    nameof(CompaniesHouseAccountsController.OfficialTestSubmitOnce));
                Assert(submitAction?.GetCustomAttribute<HttpPostAttribute>()?.Template == "official-test-submit-once"
                    && !new DisabledCompaniesHouseOfficialTestSubmission().Enabled,
                    "The controlled endpoint is absent or its default-disabled implementation is enabled.");
                var regenerateAction = typeof(CompaniesHouseAccountsController).GetMethod(
                    nameof(CompaniesHouseAccountsController.RegenerateDocument));
                Assert(regenerateAction?.GetCustomAttribute<HttpPostAttribute>()?.Template == "regenerate-document",
                    "The explicit Companies House document-regeneration endpoint is absent.");

                var statusRequest = new CompaniesHouseOfficialTestStatusRequest(
                    "S00001", "STATUS0001", true);
                var statusGateway = new PendingStatusGateway();
                var officialStatus = new CompaniesHouseOfficialTestStatus(
                    routed, statusGateway, evidenceRoot);
                var statusOutcome = await officialStatus.ExecuteOnceAsync(statusRequest);
                Assert(statusOutcome.ResponseKind == CompaniesHouseGatewayResponseKind.SubmissionStatus.ToString()
                    && statusOutcome.Status == CompaniesHouseSubmissionStatusCode.Pending
                    && statusOutcome.ExaminerComment == "Pending review of attachment"
                    && statusOutcome.ExternalRequestCount == 1
                    && !statusOutcome.RetryPermitted
                    && !statusOutcome.StatusAcknowledgementRequired
                    && !statusOutcome.TerminalFilingDecisionReceived
                    && statusGateway.ExchangeCount == 1,
                    "The official specific-submission status query did not preserve its safe pending result.");
                Assert(JsonSerializer.Serialize(statusOutcome).Contains("\"Status\":\"Pending\"",
                        StringComparison.Ordinal),
                    "The human-facing status outcome exposed the enum as a numeric implementation value.");
                Assert(Directory.GetFiles(evidenceRoot, "request.protected.xml", SearchOption.AllDirectories).Length == 2
                    && Directory.GetFiles(evidenceRoot, "response.xml", SearchOption.AllDirectories).Length == 2
                    && Directory.GetDirectories(evidenceRoot, "status-STATUS0001", SearchOption.AllDirectories).Length == 1,
                    "The status query did not retain its protected request and response beside the filing evidence.");

                var restartedGateway = new PendingStatusGateway();
                var restartedStatus = new CompaniesHouseOfficialTestStatus(
                    routed, restartedGateway, evidenceRoot);
                var recoveredStatus = await restartedStatus.ExecuteOnceAsync(statusRequest);
                Assert(recoveredStatus.SubmissionNumber == statusOutcome.SubmissionNumber
                    && recoveredStatus.TransactionId == statusOutcome.TransactionId
                    && recoveredStatus.RequestSha256 == statusOutcome.RequestSha256
                    && recoveredStatus.ResponseSha256 == statusOutcome.ResponseSha256
                    && recoveredStatus.Status == statusOutcome.Status
                    && restartedGateway.ExchangeCount == 0,
                    "A completed status query was resent instead of being recovered after restart.");

                var filingDirectory = Directory.GetDirectories(evidenceRoot, "*-S00001").Single();
                var ambiguousDirectory = Path.Combine(filingDirectory, "status-STATUS0002");
                Directory.CreateDirectory(ambiguousDirectory);
                await File.WriteAllTextAsync(Path.Combine(ambiguousDirectory, "pre-send.json"),
                    "{\"TransactionId\":\"STATUS0002\"}");
                try
                {
                    await restartedStatus.ExecuteOnceAsync(new("S00001", "STATUS0002", true));
                    throw new InvalidOperationException("An ambiguous status query was automatically retried.");
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.Contains("transaction ID remains consumed", StringComparison.Ordinal))
                {
                    assertions++;
                }
                Assert(restartedGateway.ExchangeCount == 0,
                    "An ambiguous retained status attempt reached the gateway after restart.");

                try
                {
                    await restartedStatus.ExecuteOnceAsync(new("S99999", "STATUS0003", true));
                    throw new InvalidOperationException("A status query without retained filing evidence was accepted.");
                }
                catch (InvalidOperationException exception) when (
                    exception.Message.Contains("retained filing acknowledgement", StringComparison.Ordinal))
                {
                    assertions++;
                }
                var statusAction = typeof(CompaniesHouseAccountsController).GetMethod(
                    nameof(CompaniesHouseAccountsController.OfficialTestStatusOnce));
                Assert(statusAction?.GetCustomAttribute<HttpPostAttribute>()?.Template == "official-test-status-once"
                    && !new DisabledCompaniesHouseOfficialTestStatus().Enabled,
                    "The controlled status endpoint is absent or its disabled implementation is enabled.");

                var unclassifiedRoot = Path.Combine(Path.GetTempPath(),
                    $"tax-hub-ch-unclassified-{Guid.NewGuid():N}");
                try
                {
                    var failureEventLog = new RecordingSubmissionEventLog();
                    var unclassified = new CompaniesHouseOfficialTestSubmission(
                        runner, routed, new UnclassifiedGateway(), failureEventLog,
                        unclassifiedRoot);
                    try
                    {
                        await unclassified.ExecuteOnceAsync(payload);
                        throw new InvalidOperationException("An unclassified official response was accepted.");
                    }
                    catch (InvalidOperationException exception) when (
                        exception.Message.Contains("protected raw response has been retained",
                            StringComparison.Ordinal))
                    {
                        Assert(Directory.GetFiles(unclassifiedRoot, "response.unclassified.xml",
                                   SearchOption.AllDirectories).Length == 1
                            && Directory.GetFiles(unclassifiedRoot, "ambiguous-failure.json",
                                SearchOption.AllDirectories).Length == 1,
                            "The one-shot boundary did not retain its unclassified response evidence.");
                        Assert(failureEventLog.WriteCount == 1 && failureEventLog.IsError
                            && exception.Message.Contains("Trade Control event log: TEST00001",
                                StringComparison.Ordinal),
                            "The failed official exchange did not create and expose its application error event.");
                    }
                }
                finally
                {
                    if (Directory.Exists(unclassifiedRoot))
                        Directory.Delete(unclassifiedRoot, recursive: true);
                }
            }
            finally
            {
                if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, recursive: true);
            }
        }
        finally
        {
            File.Delete(secretFile);
        }
        return assertions;
    }

    private sealed class RecordingSubmissionEventLog : ICompaniesHouseSubmissionEventLog
    {
        public int WriteCount { get; private set; }
        public bool IsError { get; private set; }
        public string AccountsPayload { get; private set; } = string.Empty;

        public Task<string> WriteAsync(string sqlConnection, PreparedSubmissionPackage package,
            string submissionNumber, string transactionId, string responseKind, string requestSha256,
            string? responseSha256, IReadOnlyList<CompaniesHouseError> errors, bool isError,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteCount++;
            IsError = isError;
            AccountsPayload = Encoding.UTF8.GetString(package.Documents.Single().Artifact.Content.ToArray());
            return Task.FromResult("TEST00001");
        }
    }

    private static PreparedSubmissionPackage Package(string submissionNumber)
    {
        XNamespace govTalk = CompaniesHouseEnvelopeSerializer.GovTalkNamespace;
        XNamespace form = CompaniesHouseEnvelopeSerializer.FormSubmissionNamespace;
        var accountsBytes = Encoding.UTF8.GetBytes(
            "<html xmlns='http://www.w3.org/1999/xhtml'><body>DATABASE-DERIVED ACCOUNTS</body></html>");
        var xml = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement(govTalk + "GovTalkMessage",
                new XElement(govTalk + "EnvelopeVersion", "1.0"),
                new XElement(govTalk + "Header", new XElement(govTalk + "MessageDetails",
                    new XElement(govTalk + "Class", "Accounts"),
                    new XElement(govTalk + "Qualifier", "request"),
                    new XElement(govTalk + "Function", "submit"))),
                new XElement(govTalk + "GovTalkDetails"),
                new XElement(govTalk + "Body", new XElement(form + "FormSubmission",
                    new XElement(form + "FormHeader",
                        new XElement(form + "CompanyNumber", "01234567"),
                        new XElement(form + "CompanyType", "EW"),
                        new XElement(form + "CompanyName", "WEBHARNESS SIMULATION LIMITED"),
                        new XElement(form + "PackageReference", "UNALLOCATED"),
                        new XElement(form + "Language", "EN"),
                        new XElement(form + "FormIdentifier", "Accounts"),
                        new XElement(form + "SubmissionNumber", submissionNumber)),
                    new XElement(form + "DateSigned", "2026-10-03"),
                    new XElement(form + "Form"),
                    new XElement(form + "Document",
                        new XElement(form + "Data", Convert.ToBase64String(accountsBytes)),
                        new XElement(form + "Filename", "accounts.xhtml"),
                        new XElement(form + "ContentType", "application/xml"),
                        new XElement(form + "Category", "ACCOUNTS"))))));
        byte[] transmissionBytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
                   { Encoding = new UTF8Encoding(false), Indent = false }))
                xml.Save(writer);
            transmissionBytes = stream.ToArray();
        }
        var findings = new[]
        {
            new PreparedArtifactFinding(PreparedFindingSeverity.Error, "CH-PENDING-CREDENTIALS",
                "Synthetic simulation supplies credentials out of band."),
            new PreparedArtifactFinding(PreparedFindingSeverity.Error, "CH-PENDING-DEVELOPER-TEST",
                "Developer-test evidence remains pending.")
        };
        var transmission = PreparedStatutoryArtifact.Create("GB", "COMPANIES-HOUSE",
            "FILE-COMPANY-ACCOUNTS", "TIS-6.0", PreparedArtifactStatus.Preview,
            "application/xml", transmissionBytes, findings: findings);
        var accounts = PreparedStatutoryArtifact.Create("GB", "COMPANIES-HOUSE",
            "STATUTORY-ACCOUNTS", "FRC-2026", PreparedArtifactStatus.Preview,
            "application/xhtml+xml", accountsBytes);
        return new("COMPANIES-HOUSE-ACCOUNTS", transmission,
            [new("accounts.xhtml", accounts)],
            new(SubmissionPollingMode.CompaniesHouseXmlGateway, "GetSubmissionStatus"));
    }

    private sealed class PreparedRunner(PreparedSubmissionPackage package) : ICompaniesHouseAccountsRunner
    {
        public CompaniesHouseAccountsPayload? Received { get; private set; }

        public Task<PreparedSubmissionPackage> PrepareAsync(CompaniesHouseAccountsPayload payload,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Received = payload;
            return Task.FromResult(package);
        }
    }

    private sealed class AcknowledgingGateway : ICompaniesHouseXmlGatewaySession
    {
        public int ExchangeCount { get; private set; }
        public string EvidenceClass => CompaniesHouseOfficialTestGatewayFactory.OfficialTestEvidence;

        public Task<CompaniesHouseGatewayResponse> ExchangeAsync(CompaniesHouseGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExchangeCount++;
            var response = Encoding.UTF8.GetBytes("""
                <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
                  <EnvelopeVersion>1.0</EnvelopeVersion>
                  <Header><MessageDetails><Class>Accounts</Class><Qualifier>acknowledgement</Qualifier><TransactionID>ACK0001</TransactionID><GatewayTimestamp>2026-10-06T16:00:00Z</GatewayTimestamp></MessageDetails></Header>
                  <GovTalkDetails/><Body/>
                </GovTalkMessage>
                """);
            return Task.FromResult(new CompaniesHouseGatewayResponse(
                CompaniesHouseGatewayResponseKind.Acknowledgement,
                System.Collections.Immutable.ImmutableArray.CreateRange(response),
                Hash(request.Content.AsSpan()), Hash(response), EvidenceClass));
        }
    }

    private sealed class UnclassifiedGateway : ICompaniesHouseXmlGatewaySession
    {
        public string EvidenceClass => CompaniesHouseOfficialTestGatewayFactory.OfficialTestEvidence;

        public Task<CompaniesHouseGatewayResponse> ExchangeAsync(CompaniesHouseGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = Encoding.UTF8.GetBytes("""
                <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
                  <EnvelopeVersion>1.0</EnvelopeVersion>
                  <Header><MessageDetails><Class>Accounts</Class><Qualifier>response</Qualifier></MessageDetails></Header>
                  <GovTalkDetails/><Body/>
                </GovTalkMessage>
                """);
            throw new CompaniesHouseUnclassifiedResponseException(
                "The Companies House response does not match the requested operation.",
                System.Collections.Immutable.ImmutableArray.CreateRange(response), Hash(response),
                new InvalidDataException("Unclassified test response."));
        }
    }

    private sealed class PendingStatusGateway : ICompaniesHouseXmlGatewaySession
    {
        public int ExchangeCount { get; private set; }
        public string EvidenceClass => CompaniesHouseOfficialTestGatewayFactory.OfficialTestEvidence;

        public Task<CompaniesHouseGatewayResponse> ExchangeAsync(CompaniesHouseGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Operation != CompaniesHouseGatewayOperation.GetSubmissionStatus)
                throw new InvalidOperationException("The status test received a non-status operation.");
            ExchangeCount++;
            var response = Encoding.UTF8.GetBytes("""
                <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
                  <EnvelopeVersion>1.0</EnvelopeVersion>
                  <Header><MessageDetails><Class>GetSubmissionStatus</Class><Qualifier>response</Qualifier><TransactionID>STATUS0001</TransactionID><GatewayTimestamp>2026-10-07T09:00:00Z</GatewayTimestamp></MessageDetails></Header>
                  <GovTalkDetails/><Body><SubmissionStatus><Status><SubmissionNumber>S00001</SubmissionNumber><StatusCode>PENDING</StatusCode><CompanyNumber>01234567</CompanyNumber><Examiner><Telephone>Fred Blogs</Telephone><Comment>Pending review of attachment</Comment></Examiner></Status></SubmissionStatus></Body>
                </GovTalkMessage>
                """);
            return Task.FromResult(new CompaniesHouseGatewayResponse(
                CompaniesHouseGatewayResponseKind.SubmissionStatus,
                System.Collections.Immutable.ImmutableArray.CreateRange(response),
                Hash(request.Content.AsSpan()), Hash(response), EvidenceClass));
        }
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));
}
