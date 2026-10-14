using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using System.Net;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;

internal static class TransactionEngineTests
{
    private const string Correlation = "46DCD4CC7E194088B99857931C185829";
    private const string TransactionId = "ABCDEF0123456789ABCDEF0123456789";

    internal static async Task<int> RunAsync(string root)
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        async Task RejectAsync(Func<Task> action, string message)
        {
            assertions++;
            try { await action(); }
            catch (ArgumentException) { return; }
            catch (InvalidDataException) { return; }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(message);
        }

        var options = TransactionEngineProtocolOptions.CorporationTaxFixture();
        var official = Convert.FromBase64String(OfficialIrmarkSubmissionBase64);
        Assert(Convert.ToHexString(SHA256.HashData(official))
                == "E6A38D07BDD1F400A34E8BFD95A69B1718F4F10A3B01BA04602788287CA353E9"
            && TransactionEngineWire.CalculateIrmark(official) == "RPfWtxHeCZRcwfitnIJmK9xc4OQ=",
            "The Gateway IRmark algorithm does not reproduce HMRC's published example.");

        var package = Package();
        var original = package.Transmission.Content.ToArray();
        var identity = new TransactionEngineSubmissionIdentity(
            "6000", "fixture-user", "fixture-password", "1234567890", "Trade Control", "5.9");
        var materialized = TransactionEngineWire.Materialize(package,
            identity, TransactionId, options);
        Assert(package.Transmission.Content.AsSpan().SequenceEqual(original)
            && materialized.TransactionId == TransactionId
            && materialized.ServiceRootSha256 == package.Transmission.Sha256
            && materialized.WireSha256 == Convert.ToHexString(SHA256.HashData(materialized.Content.AsSpan()))
            && materialized.AttachmentSha256.SequenceEqual(package.Documents.Select(x => x.Artifact.Sha256)),
            "Materialisation did not preserve the immutable service-root/attachment evidence chain.");
        var wire = XDocument.Parse(Encoding.UTF8.GetString(materialized.Content.AsSpan()),
            LoadOptions.PreserveWhitespace);
        XNamespace ct = TransactionEngineWire.CorporationTaxNamespace;
        Assert(wire.Descendants(ct + "IRmark").Single().Value == materialized.Irmark
            && materialized.Irmark.Length == 28
            && wire.Descendants(XNamespace.Get(TransactionEngineWire.GovTalkNamespace) + "URI").Single().Value == "6000"
            && TransactionEngineWire.CalculateIrmark(materialized.Content.AsSpan()) == materialized.Irmark,
            "The calculated IRmark was not inserted into the one reserved CT service-root slot.");
        Assert(!Encoding.UTF8.GetString(original).Contains(materialized.Irmark, StringComparison.Ordinal)
            && package.Transmission.Status == PreparedArtifactStatus.Preview,
            "Fixture materialisation mutated or promoted the Objective 3 preview artifact.");

        var credentialRoot = Path.Combine(root, "transaction-engine-credentials");
        Directory.CreateDirectory(credentialRoot);
        var registrationEmail = Path.Combine(credentialRoot, "registration.txt");
        var passwordFile = Path.Combine(credentialRoot, "passwords.txt");
        await File.WriteAllTextAsync(registrationEmail, """
            Your organisation's Vendor ID is 4321.
            <SenderID>test-sender<SenderID>
            <Key Type="UTR">1234567890</Key>
            """);
        await File.WriteAllTextAsync(passwordFile, "[1] test-password\n[2] unused-password");
        var protectedCredentials = new HmrcCorporationTaxTestCredentialProvider(
            registrationEmail, passwordFile);
        var officialTest = await new CorporationTaxOfficialTestMaterializer(protectedCredentials)
            .MaterializeAsync(package, TransactionId, "Trade Control", "5.10");
        var officialWire = XDocument.Parse(Encoding.UTF8.GetString(officialTest.Content.AsSpan()));
        Assert(officialTest.EvidenceClass == CorporationTaxOfficialTestMaterialization.MaterializedEvidence
            && officialTest.IntendedEndpoint == "https://test-transaction-engine.tax.service.gov.uk/submission"
            && officialTest.MessageClass == "HMRC-CT-CT600"
            && officialTest.ServiceArtifactSha256 == package.Transmission.Sha256
            && officialWire.Descendants(XNamespace.Get(TransactionEngineWire.GovTalkNamespace) + "URI").Single().Value == "4321"
            && officialWire.Descendants(XNamespace.Get(TransactionEngineWire.GovTalkNamespace) + "GatewayTest").Single().Value == "1"
            && !officialTest.ToString().Contains("test-password", StringComparison.Ordinal),
            "The protected ETS materialiser did not produce a send-disabled external-test request safely.");

        var externalOptions = TransactionEngineProtocolOptions.CorporationTaxExternalTest();
        var outbound = new TransactionEngineOutboundMessage(TransactionEngineOutboundKind.Submit,
            externalOptions.SubmissionEndpoint, officialTest.Content, officialTest.WireSha256);
        var responseBytes = Message("acknowledgement", "submit", Correlation,
            "<ResponseEndPoint PollInterval=\"10\">https://test-transaction-engine.tax.service.gov.uk/poll</ResponseEndPoint>");
        var recording = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseBytes)
        });
        var exchange = await new TransactionEngineHttpGateway(new HttpClient(recording),
                externalOptions, new())
            .ExchangeOnceAsync(outbound);
        Assert(recording.CallCount == 1
            && recording.Method == HttpMethod.Post
            && recording.RequestUri == externalOptions.SubmissionEndpoint
            && recording.MediaType == "application/xml"
            && recording.RequestBody.AsSpan().SequenceEqual(officialTest.Content.AsSpan())
            && exchange.Content.AsSpan().SequenceEqual(responseBytes)
            && exchange.ResponseSha256 == Convert.ToHexString(SHA256.HashData(responseBytes)),
            "The bounded one-attempt ETS HTTP boundary changed the wire request or response evidence.");
        var redirecting = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers = { Location = new Uri("https://evil.example/") }
        });
        await RejectAsync(() => new TransactionEngineHttpGateway(new HttpClient(redirecting),
                externalOptions, new()).ExchangeOnceAsync(outbound),
            "The ETS transport accepted a redirect.");

        await RejectAsync(() => Task.Run(() => TransactionEngineWire.CalculateIrmark(
            "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>"u8)),
            "An XML DTD/external entity reached IRmark processing.");
        await RejectAsync(() => Task.Run(() => TransactionEngineWire.CalculateIrmark(new byte[2049], 2048)),
            "Oversized XML reached IRmark processing.");
        await RejectAsync(() => Task.Run(() => TransactionEngineWire.ValidateResponseEndpoint(
            "https://transaction-engine.tax.service.gov.uk.evil.example/submission", options)),
            "A suffix-confused Transaction Engine response host was trusted.");
        await RejectAsync(() => Task.Run(() => TransactionEngineWire.ValidateResponseEndpoint(
            "http://transaction-engine.tax.service.gov.uk/submission", options)),
            "A downgraded Transaction Engine response endpoint was trusted.");

        var ack = TransactionEngineResponseParser.Parse(Message("acknowledgement", "submit", Correlation,
            "<ResponseEndPoint PollInterval=\"10\">https://transaction-engine.tax.service.gov.uk/submission</ResponseEndPoint>"), options);
        Assert(ack.Kind == TransactionEngineResponseKind.Acknowledgement
            && ack.CorrelationId == Correlation && ack.PollIntervalSeconds == 10
            && ack.ResponseEndpoint == options.SubmissionEndpoint,
            "The official acknowledgement fields were not parsed and constrained.");
        await RejectAsync(() => Task.Run(() => TransactionEngineResponseParser.Parse(
            Message("acknowledgement", "submit", Correlation,
                "<ResponseEndPoint PollInterval=\"10\">https://evil.example/submission</ResponseEndPoint>"), options)),
            "An acknowledgement supplied an untrusted polling endpoint.");

        var gatewayError = TransactionEngineResponseParser.Parse(Message("error", "submit", null, string.Empty,
            "<GovTalkErrors><Error><RaisedBy>Gateway</RaisedBy><Number>1046</Number><Type>fatal</Type><Text>Authentication failed</Text></Error></GovTalkErrors>"), options);
        var businessError = TransactionEngineResponseParser.Parse(Message("error", "submit", Correlation,
            string.Empty,
            "<GovTalkErrors><Error><RaisedBy>department</RaisedBy><Number>3001</Number><Type>business</Type></Error></GovTalkErrors>",
            "<ErrorResponse xmlns=\"http://www.govtalk.gov.uk/CM/errorresponse\"><Error><RaisedBy>ChRIS</RaisedBy><Number>2022</Number><Type>business</Type><Text>IRmark not found.</Text></Error></ErrorResponse>"), options);
        Assert(gatewayError.Kind == TransactionEngineResponseKind.GatewayError
            && businessError.Kind == TransactionEngineResponseKind.BusinessError
            && businessError.Errors.Any(error => error.Number == "2022"),
            "Gateway failures and departmental business errors were not distinguished.");

        var success = TransactionEngineResponseParser.Parse(Message("response", "submit", Correlation,
            string.Empty, string.Empty,
            "<SuccessResponse xmlns=\"http://www.inlandrevenue.gov.uk/SuccessResponse\"><IRmarkReceipt><Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\" /></IRmarkReceipt></SuccessResponse>"), options);
        Assert(success.Kind == TransactionEngineResponseKind.BusinessResponse
            && success.ContainsIrmarkReceipt && success.ResponseSha256.Length == 64,
            "A terminal response did not retain its receipt marker and exact response digest.");

        var dataRequest = XDocument.Parse(Encoding.UTF8.GetString(TransactionEngineWire.DataRequest(
            identity, options)));
        XNamespace gov = TransactionEngineWire.GovTalkNamespace;
        Assert(dataRequest.Descendants(gov + "Function").Single().Value == "list"
            && dataRequest.Descendants(XNamespace.Get("urn:gateway/statusrequest") + "IncludeIdentifiers").Single().Value == "1",
            "Lost-reply recovery is not represented by an authenticated DATA_REQUEST.");
        var dataResponseBytes = Message("response", "list", null, string.Empty, string.Empty,
            $"<StatusReport xmlns=\"urn:gateway/statusrequest\"><SenderID>fixture-user</SenderID><StartTimeStamp></StartTimeStamp><EndTimeStamp></EndTimeStamp><StatusRecord><TimeStamp>2026-10-14T08:00:00Z</TimeStamp><CorrelationID>{Correlation}</CorrelationID><TransactionID>{TransactionId}</TransactionID><Status>SUBMISSION_ACKNOWLEDGE</Status></StatusRecord></StatusReport>");
        var dataResponse = TransactionEngineResponseParser.Parse(dataResponseBytes, options);
        Assert(TransactionEngineResponseParser.MatchRecoveryRecord(dataResponse, TransactionId).CorrelationId
                == Correlation,
            "DATA_RESPONSE could not uniquely reconcile a lost acknowledgement by durable transaction ID.");

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 14, 8, 0, 0, TimeSpan.Zero));
        var metadata = Path.Combine(root, "transaction-engine", "conversations.json");
        var store = new FileTransactionEngineConversationStore(new(metadata), clock);
        var contentStore = new FileSubmissionContentStore(new(
            Path.Combine(root, "transaction-engine", "evidence"), MaximumPayloadBytes: 1024 * 1024,
            MaximumResponseBytes: 1024 * 1024));
        var handler = new TransactionEngineFixtureHandler(store, contentStore, options, clock);
        var created = await handler.PrepareAsync(package, identity, TransactionId,
            "tenant-a", "principal-a", "ct:1234567890:2026-06-30");
        var outboundSubmission = await handler.BeginSubmissionAsync(
            "tenant-a", "principal-a", created.ConversationReference);
        var uncertain = await store.GetAsync("tenant-a", "principal-a", created.ConversationReference);
        Assert(uncertain is not null
            && uncertain.Stage == TransactionEngineConversationStage.SubmissionUncertain
            && uncertain.RequiresDataRequest
            && outboundSubmission.Kind == TransactionEngineOutboundKind.Submit
            && outboundSubmission.Content.AsSpan().SequenceEqual(materialized.Content.AsSpan())
            && outboundSubmission.Sha256 == materialized.WireSha256,
            "The conversation was not durably marked uncertain before its first send.");
        await RejectAsync(() => handler.BeginSubmissionAsync(
                "tenant-a", "principal-a", created.ConversationReference),
            "A restart could issue a duplicate new submission after an uncertain send.");
        var recoveryRequest = await handler.BuildDataRequestAsync(
            "tenant-a", "principal-a", created.ConversationReference, identity);
        Assert(recoveryRequest.Kind == TransactionEngineOutboundKind.DataRequest,
            "The fixture handler did not expose DATA_REQUEST after an uncertain send.");

        var restarted = new FileTransactionEngineConversationStore(new(metadata), clock);
        var restartedHandler = new TransactionEngineFixtureHandler(restarted, contentStore, options, clock);
        var recovered = await restarted.GetAsync("tenant-a", "principal-a", created.ConversationReference);
        Assert(recovered?.RequiresDataRequest == true && recovered.WireSha256 == materialized.WireSha256,
            "Restart recovery lost the uncertain state or immutable wire digest.");
        Assert(recovered?.TransactionId == TransactionId,
            "Restart recovery lost the client transaction ID required by DATA_REQUEST reconciliation.");
        var polling = await restartedHandler.ApplyDataResponseAsync("tenant-a", "principal-a",
            created.ConversationReference, dataResponseBytes);
        Assert(!polling.MayPoll(clock.GetUtcNow())
            && polling.ResponseEvidence?.Count == 1
            && polling.ResponseEvidence[0].Sha256
                == Convert.ToHexString(SHA256.HashData(dataResponseBytes)),
            "Recovery response evidence was not retained or polling was permitted before HMRC's interval elapsed.");
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert((await restarted.GetAsync("tenant-a", "principal-a", created.ConversationReference))!
            .MayPoll(clock.GetUtcNow()), "Polling did not become eligible at HMRC's interval.");
        var outboundPoll = await restartedHandler.BuildPollAsync(
            "tenant-a", "principal-a", created.ConversationReference);
        Assert(outboundPoll.Kind == TransactionEngineOutboundKind.Poll
            && outboundPoll.Endpoint == options.SubmissionEndpoint,
            "The recovered conversation did not produce a constrained correlated poll.");
        await RejectAsync(() => restarted.RecordTerminalAsync("tenant-a", "principal-a",
            created.ConversationReference, success with { CorrelationId = new string('A', 32) },
            "response/wrong.bin", "response/wrong.bin"),
            "A terminal response crossed the correlation boundary.");

        var exactResponse = Message("response", "submit", Correlation, string.Empty, string.Empty,
            "<SuccessResponse xmlns=\"http://www.inlandrevenue.gov.uk/SuccessResponse\"><IRmarkReceipt><Signature xmlns=\"http://www.w3.org/2000/09/xmldsig#\" /></IRmarkReceipt></SuccessResponse>");
        var responseReference = await contentStore.StoreAsync("tenant-a", "principal-a",
            SubmissionContentKind.Response, exactResponse);
        await RejectAsync(() => restarted.RecordTerminalAsync("tenant-a", "principal-a",
            created.ConversationReference, success, responseReference, null),
            "Deletion could proceed without retention of a returned signed receipt.");
        var terminal = await restarted.RecordTerminalAsync("tenant-a", "principal-a",
            created.ConversationReference, success, responseReference, responseReference);
        var readResponse = await contentStore.ReadAsync("tenant-a", "principal-a", responseReference);
        Assert(terminal.MayDelete && readResponse is not null && readResponse.AsSpan().SequenceEqual(exactResponse)
            && terminal.TerminalResponseSha256 == Convert.ToHexString(SHA256.HashData(exactResponse)),
            "The exact terminal response/receipt was not retained before delete.");

        var deleting = await restarted.BeginDeleteAsync("tenant-a", "principal-a", created.ConversationReference);
        Assert(deleting.Stage == TransactionEngineConversationStage.DeletePending
            && deleting.Outcome == nameof(TransactionEngineResponseKind.BusinessResponse),
            "Beginning deletion changed or discarded the terminal business outcome.");
        var afterDeleteFailure = await new FileTransactionEngineConversationStore(new(metadata), clock)
            .GetAsync("tenant-a", "principal-a", created.ConversationReference);
        Assert(afterDeleteFailure?.Stage == TransactionEngineConversationStage.DeletePending,
            "A delete failure/restart lost the independently durable terminal result.");
        var deleteResponse = TransactionEngineResponseParser.Parse(
            Message("response", "delete", Correlation), options);
        var complete = await restarted.RecordDeleteResponseAsync("tenant-a", "principal-a",
            created.ConversationReference, deleteResponse);
        Assert(complete.Stage == TransactionEngineConversationStage.Complete
            && complete.TerminalResponseReference == responseReference,
            "DELETE_RESPONSE did not complete the protocol while retaining terminal evidence.");

        var poll = XDocument.Parse(Encoding.UTF8.GetString(TransactionEngineWire.Poll(Correlation, options)));
        var delete = XDocument.Parse(Encoding.UTF8.GetString(TransactionEngineWire.Delete(Correlation, options)));
        Assert(poll.Descendants(gov + "Function").Single().Value == "poll"
            && delete.Descendants(gov + "Function").Single().Value == "delete"
            && poll.Descendants(gov + "CorrelationID").Single().Value == Correlation,
            "Poll/delete control messages do not preserve the conversation class and correlation ID.");

        return assertions;
    }

    private static PreparedSubmissionPackage Package()
    {
        var service = PreparedStatutoryArtifact.Create("UK", "HMRC", "HMRC-CT600-SERVICE-ROOT",
            "CT600-V3-2026-RIM-1.994+CT-COMP-2025-V1.0.0", PreparedArtifactStatus.Preview,
            "application/xml", """
            <?xml version="1.0" encoding="utf-8"?><IRenvelope xmlns="http://www.govtalk.gov.uk/taxation/CT/5"><IRheader><Keys><Key Type="UTR">1234567890</Key></Keys><PeriodEnd>2026-06-30</PeriodEnd><IRmark Type="generic" /><Sender>Company</Sender></IRheader><CompanyTaxReturn ReturnType="new"><CompanyInformation><CompanyName>Example Limited</CompanyName></CompanyInformation></CompanyTaxReturn></IRenvelope>
            """u8);
        var computation = PreparedStatutoryArtifact.Create("UK", "HMRC", "CT-COMPUTATION",
            "CT-COMP-2025-V1.0.0", PreparedArtifactStatus.Preview, "application/xhtml+xml",
            "<html>computation</html>"u8);
        var accounts = PreparedStatutoryArtifact.Create("UK", "HMRC", "CT-ACCOUNTS",
            "FRC-2026", PreparedArtifactStatus.Preview, "application/xhtml+xml",
            "<html>accounts</html>"u8);
        return new("HMRC-CORPORATION-TAX", service,
            [new("computation", computation), new("accounts", accounts)],
            new(SubmissionPollingMode.TransactionEngine));
    }

    private static byte[] Message(string qualifier, string function, string? correlation,
        string extraDetails = "", string govTalkDetails = "", string body = "") => Encoding.UTF8.GetBytes($"""
        <?xml version="1.0" encoding="utf-8"?><GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope"><EnvelopeVersion>2.0</EnvelopeVersion><Header><MessageDetails><Class>HMRC-CT-CT600</Class><Qualifier>{qualifier}</Qualifier><Function>{function}</Function>{(correlation is null ? "" : $"<CorrelationID>{correlation}</CorrelationID>")}{extraDetails}</MessageDetails><SenderDetails /></Header><GovTalkDetails><Keys />{govTalkDetails}</GovTalkDetails><Body>{body}</Body></GovTalkMessage>
        """);

    // Exact published bytes (SHA-256 E6A38D07BDD1F400A34E8BFD95A69B1718F4F10A3B01BA04602788287CA353E9).
    // Base64 avoids newline conversion because whitespace is material to C14N.
    private const string OfficialIrmarkSubmissionBase64 =
        "PEdvdlRhbGtNZXNzYWdlIHhtbG5zPSJodHRwOi8vd3d3LmdvdnRhbGsuZ292LnVrL0NNL2VudmVsb3BlIj4KCTxFbnZlbG9wZVZlcnNpb24+Mi4wPC9FbnZlbG9wZVZlcnNpb24+Cgk8SGVhZGVyPgoJCTxNZXNzYWdlRGV0YWlscz4KCQkJPENsYXNzPklSLUNURi1GSXJldHVybjwvQ2xhc3M+CgkJCTxRdWFsaWZpZXI+cmVxdWVzdDwvUXVhbGlmaWVyPgoJCQk8RnVuY3Rpb24+c3VibWl0PC9GdW5jdGlvbj4KCQkJPENvcnJlbGF0aW9uSUQvPgoJCQk8VHJhbnNmb3JtYXRpb24+WE1MPC9UcmFuc2Zvcm1hdGlvbj4KCQkJPEdhdGV3YXlUZXN0PjE8L0dhdGV3YXlUZXN0PgoJCQk8R2F0ZXdheVRpbWVzdGFtcC8+CgkJPC9NZXNzYWdlRGV0YWlscz4KCQk8U2VuZGVyRGV0YWlscz4KCQkJPElEQXV0aGVudGljYXRpb24+CgkJCQk8U2VuZGVySUQ+Y3RmdXNlcjAwMTwvU2VuZGVySUQ+CgkJCQk8QXV0aGVudGljYXRpb24+CgkJCQkJPE1ldGhvZD5jbGVhcjwvTWV0aG9kPgoJCQkJCTxSb2xlPnByaW5jaXBhbDwvUm9sZT4KCQkJCQk8VmFsdWU+dGVzdGluZzE8L1ZhbHVlPgoJCQkJPC9BdXRoZW50aWNhdGlvbj4KCQkJPC9JREF1dGhlbnRpY2F0aW9uPgoJCTwvU2VuZGVyRGV0YWlscz4KCTwvSGVhZGVyPgoJPEdvdlRhbGtEZXRhaWxzPgoJCTxLZXlzPgoJCQk8S2V5IFR5cGU9IkNURlByb3ZpZGVySUQiPlRFUzMwMDE8L0tleT4KCQk8L0tleXM+CgkJPFRhcmdldERldGFpbHM+CgkJCTxPcmdhbmlzYXRpb24+SVI8L09yZ2FuaXNhdGlvbj4KCQk8L1RhcmdldERldGFpbHM+CgkJPENoYW5uZWxSb3V0aW5nPgoJCQk8Q2hhbm5lbD4KCQkJCTxVUkk+MzAwMTwvVVJJPgoJCQkJPFByb2R1Y3Q+U0RTIFRlYW08L1Byb2R1Y3Q+CgkJCQk8VmVyc2lvbj4xLjA8L1ZlcnNpb24+CgkJCTwvQ2hhbm5lbD4KCQk8L0NoYW5uZWxSb3V0aW5nPgoJPC9Hb3ZUYWxrRGV0YWlscz4KCTxCb2R5PgoJCTxJUmVudmVsb3BlIHhtbG5zPSJodHRwOi8vd3d3LmdvdnRhbGsuZ292LnVrL3RheGF0aW9uL0NURiI+CgkJCTxJUmhlYWRlcj4KCQkJCTxUZXN0TWVzc2FnZT4wPC9UZXN0TWVzc2FnZT4KCQkJCTxLZXlzPgoJCQkJCTxLZXkgVHlwZT0iQ1RGUHJvdmlkZXJJRCI+VEVTMzAwMTwvS2V5PgoJCQkJPC9LZXlzPgoJCQkJPFBlcmlvZEVuZD4yMDA0LTA0LTE1PC9QZXJpb2RFbmQ+CgkJCQk8RGVmYXVsdEN1cnJlbmN5PkdCUDwvRGVmYXVsdEN1cnJlbmN5PgoJCQkJPE1hbmlmZXN0PgoJCQkJCTxDb250YWlucz4KCQkJCQkJPFJlZmVyZW5jZT4KCQkJCQkJCTxOYW1lc3BhY2U+aHR0cDovL3d3dy5nb3Z0YWxrLmdvdi51ay90YXhhdGlvbi9DVEY8L05hbWVzcGFjZT4KCQkJCQkJCTxTY2hlbWFWZXJzaW9uPjIwMDUtdjEuMjwvU2NoZW1hVmVyc2lvbj4KCQkJCQkJCTxUb3BFbGVtZW50TmFtZT5GSXJldHVybjwvVG9wRWxlbWVudE5hbWU+CgkJCQkJCTwvUmVmZXJlbmNlPgoJCQkJCTwvQ29udGFpbnM+CgkJCQk8L01hbmlmZXN0PgoJCQkJPElSbWFyayBUeXBlPSJnZW5lcmljIj5SUGZXdHhIZUNaUmN3Zml0bklKbUs5eGM0T1E9PC9JUm1hcms+CgkJCQk8U2VuZGVyPkNvbXBhbnk8L1NlbmRlcj4KCQkJPC9JUmhlYWRlcj4KCQkJPEZJcmV0dXJuPgoJCQkJPFVuY29tcHJlc3NlZD4KCQkJCQk8Q2hpbGREZWF0aD4KCQkJCQkJPFVSTj5BQjEyNDY3NEE8L1VSTj4KCQkJCQkJPERPQj4yMDA0LTA1LTI0PC9ET0I+CgkJCQkJCTxET0Q+MjAwNC0wOC0xMDwvRE9EPgoJCQkJCTwvQ2hpbGREZWF0aD4KCQkJCQk8Q2hpbGREZWF0aD4KCQkJCQkJPFVSTj5CRjQ3ODU3NUQ8L1VSTj4KCQkJCQkJPERPQj4yMDAzLTEyLTI0PC9ET0I+CgkJCQkJCTxET0Q+MjAwNC0wMi0yOTwvRE9EPgoJCQkJCTwvQ2hpbGREZWF0aD4KCQkJCQk8Vm91Y2hlcj4KCQkJCQkJPFVSTj5DRDU3NzQ0NUI8L1VSTj4KCQkJCQkJPERPQj4yMDAzLTEwLTIxPC9ET0I+CgkJCQkJCTxBbW91bnQ+MjUwLjAwPC9BbW91bnQ+CgkJCQkJPC9Wb3VjaGVyPgoJCQkJCTxWb3VjaGVyPgoJCQkJCQk8VVJOPkRBNTI0NTY0IDwvVVJOPgoJCQkJCQk8RE9CPjIwMDMtMDctMTg8L0RPQj4KCQkJCQkJPEFtb3VudD4yNzcuMDA8L0Ftb3VudD4KCQkJCQk8L1ZvdWNoZXI+CgkJCQkJPFJBQT4KCQkJCQkJPFVSTj5BQzQ2ODUzNkM8L1VSTj4KCQkJCQkJPERPQj4yMDA0LTAyLTExPC9ET0I+CgkJCQkJCTxSZWY+MTIxMzQxMjE8L1JlZj4KCQkJCQkJPEFtb3VudD4yNTAuMDA8L0Ftb3VudD4KCQkJCQk8L1JBQT4KCQkJCQk8UkFBPgoJCQkJCQk8VVJOPkZBOTg2NTMyRDwvVVJOPgoJCQkJCQk8RE9CPjIwMDMtMDQtMzA8L0RPQj4KCQkJCQkJPFJlZj4yMzU0ODc1MTwvUmVmPgoJCQkJCQk8QW1vdW50PjI2OC4wMDwvQW1vdW50PgoJCQkJCTwvUkFBPgoJCQkJCTxFbmRvd21lbnQ+CgkJCQkJCTxVUk4+REUyMzQ1NjhEPC9VUk4+CgkJCQkJCTxET0I+MjAwNC0wOC0yNjwvRE9CPgoJCQkJCQk8UmVmPjI0NTg3NDU2PC9SZWY+CgkJCQkJCTxUeXBlPjE8L1R5cGU+CgkJCQkJCTxBbW91bnQ+MjUwLjAwPC9BbW91bnQ+CgkJCQkJPC9FbmRvd21lbnQ+CgkJCQkJPEVuZG93bWVudD4KCQkJCQkJPFVSTj5DRjU2OTg5N0E8L1VSTj4KCQkJCQkJPERPQj4yMDAzLTExLTE0PC9ET0I+CgkJCQkJCTxSZWY+NjU4NjU0Nzg8L1JlZj4KCQkJCQkJPFR5cGU+MjwvVHlwZT4KCQkJCQkJPEFtb3VudD4yNTYuMDA8L0Ftb3VudD4KCQkJCQk8L0VuZG93bWVudD4KCQkJCQk8VHJhbnNmZXJyZWRJbj4KCQkJCQkJPFVSTj5HQTQ1Nzg2OUI8L1VSTj4KCQkJCQkJPERPQj4yMDA0LTAxLTAyPC9ET0I+CgkJCQkJPC9UcmFuc2ZlcnJlZEluPgoJCQkJCTxUcmFuc2ZlcnJlZEluPgoJCQkJCQk8VVJOPkFGNjkzMjU0QjwvVVJOPgoJCQkJCQk8RE9CPjIwMDMtMDUtMjQ8L0RPQj4KCQkJCQk8L1RyYW5zZmVycmVkSW4+CgkJCQkJPEV4Y2VwdGlvbj4KCQkJCQkJPFVSTj5BRDIzNTg0NyA8L1VSTj4KCQkJCQkJPERPQj4yMDA0LTA3LTA0PC9ET0I+CgkJCQkJCTxSZWY+MjE0NTg5MzI8L1JlZj4KCQkJCQkJPFJlYXNvbj4xPC9SZWFzb24+CgkJCQkJPC9FeGNlcHRpb24+CgkJCQkJPEV4Y2VwdGlvbj4KCQkJCQkJPFVSTj5CRTEyNjU5OEI8L1VSTj4KCQkJCQkJPERPQj4yMDAzLTA1LTI1PC9ET0I+CgkJCQkJCTxSZWY+MzY4NzE1Njk8L1JlZj4KCQkJCQkJPFJlYXNvbj4yPC9SZWFzb24+CgkJCQkJPC9FeGNlcHRpb24+CgkJCQk8L1VuY29tcHJlc3NlZD4KCQkJPC9GSXJldHVybj4KCQk8L0lSZW52ZWxvcGU+Cgk8L0JvZHk+CjwvR292VGFsa01lc3NhZ2U+";

    // A compact readable companion to the exact fixture above.
    private const string OfficialIrmarkSubmission = """
<GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
	<EnvelopeVersion>2.0</EnvelopeVersion>
	<Header><MessageDetails><Class>IR-CTF-FIreturn</Class><Qualifier>request</Qualifier><Function>submit</Function></MessageDetails></Header>
	<GovTalkDetails><Keys><Key Type="CTFProviderID">TES3001</Key></Keys></GovTalkDetails>
	<Body>
		<IRenvelope xmlns="http://www.govtalk.gov.uk/taxation/CTF">
			<IRheader>
				<TestMessage>0</TestMessage>
				<Keys><Key Type="CTFProviderID">TES3001</Key></Keys>
				<PeriodEnd>2004-04-15</PeriodEnd>
				<DefaultCurrency>GBP</DefaultCurrency>
				<Manifest><Contains><Reference><Namespace>http://www.govtalk.gov.uk/taxation/CTF</Namespace><SchemaVersion>2005-v1.2</SchemaVersion><TopElementName>FIreturn</TopElementName></Reference></Contains></Manifest>
				<IRmark Type="generic">RPfWtxHeCZRcwfitnIJmK9xc4OQ=</IRmark>
				<Sender>Company</Sender>
			</IRheader>
			<FIreturn><Uncompressed><ChildDeath><URN>AB124674A</URN><DOB>2004-05-24</DOB><DOD>2004-08-10</DOD></ChildDeath><ChildDeath><URN>BF478575D</URN><DOB>2003-12-24</DOB><DOD>2004-02-29</DOD></ChildDeath><Voucher><URN>CD577445B</URN><DOB>2003-10-21</DOB><Amount>250.00</Amount></Voucher><Voucher><URN>DA524564 </URN><DOB>2003-07-18</DOB><Amount>277.00</Amount></Voucher><RAA><URN>AC468536C</URN><DOB>2004-02-11</DOB><Ref>12134121</Ref><Amount>250.00</Amount></RAA><RAA><URN>FA986532D</URN><DOB>2003-04-30</DOB><Ref>23548751</Ref><Amount>268.00</Amount></RAA><Endowment><URN>DE234568D</URN><DOB>2004-08-26</DOB><Ref>24587456</Ref><Type>1</Type><Amount>250.00</Amount></Endowment><Endowment><URN>CF569897A</URN><DOB>2003-11-14</DOB><Ref>65865478</Ref><Type>2</Type><Amount>256.00</Amount></Endowment><TransferredIn><URN>GA457869B</URN><DOB>2004-01-02</DOB></TransferredIn><TransferredIn><URN>AF693254B</URN><DOB>2003-05-24</DOB></TransferredIn><Exception><URN>AD235847 </URN><DOB>2004-07-04</DOB><Ref>21458932</Ref><Reason>1</Reason></Exception><Exception><URN>BE126598B</URN><DOB>2003-05-25</DOB><Ref>36871569</Ref><Reason>2</Reason></Exception></Uncompressed></FIreturn>
		</IRenvelope>
	</Body>
</GovTalkMessage>
""";

    private sealed class FakeTimeProvider(DateTimeOffset current) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => current;
        internal void Advance(TimeSpan amount) => current = current.Add(amount);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        internal int CallCount { get; private set; }
        internal HttpMethod? Method { get; private set; }
        internal Uri? RequestUri { get; private set; }
        internal string? MediaType { get; private set; }
        internal byte[] RequestBody { get; private set; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            MediaType = request.Content?.Headers.ContentType?.MediaType;
            RequestBody = request.Content is null
                ? []
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return response;
        }
    }
}
