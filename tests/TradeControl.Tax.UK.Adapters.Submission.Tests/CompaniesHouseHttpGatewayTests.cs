using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

internal static class CompaniesHouseHttpGatewayTests
{
    public static async Task<int> RunAsync()
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var requestBytes = Encoding.UTF8.GetBytes(
            "<GovTalkMessage xmlns='http://www.govtalk.gov.uk/CM/envelope'><EnvelopeVersion>1.0</EnvelopeVersion></GovTalkMessage>");
        var acknowledgementBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
              <EnvelopeVersion>1.0</EnvelopeVersion>
              <Header><MessageDetails><Class>Accounts</Class><Qualifier>acknowledgement</Qualifier><Function>submit</Function><TransactionID>OFFLINE0001</TransactionID><GatewayTimestamp>2026-10-06T15:00:00+00:00</GatewayTimestamp></MessageDetails></Header>
              <GovTalkDetails/><Body/>
            </GovTalkMessage>
            """);
        var handler = new CompaniesHouseRecordingHandler(HttpStatusCode.OK, acknowledgementBytes);
        using (var client = new HttpClient(handler))
        {
            var gateway = new CompaniesHouseOfficialTestHttpGateway(client);
            var response = await gateway.ExchangeAsync(new(CompaniesHouseGatewayOperation.FormSubmission,
                ImmutableArray.CreateRange(requestBytes)));
            Assert(response.Kind == CompaniesHouseGatewayResponseKind.Acknowledgement
                && response.EvidenceClass == CompaniesHouseOfficialTestHttpGateway.OfflineEvidence
                && response.RequestSha256 == Hash(requestBytes)
                && response.ResponseSha256 == Hash(acknowledgementBytes),
                "The offline HTTP gateway did not classify and identify the acknowledgement exactly.");
            Assert(handler.Request?.Method == HttpMethod.Post
                && handler.Request.RequestUri == CompaniesHouseGatewayEndpoints.OfficialTest
                && handler.ContentType == "text/xml; charset=utf-8"
                && handler.Content is not null && handler.Content.AsSpan().SequenceEqual(requestBytes),
                "The offline HTTP gateway changed the pinned endpoint, method, media type or exact request bytes.");
            var acknowledgement = new CompaniesHouseEnvelopeSerializer()
                .ParseGatewayAcknowledgement(response.Content.AsSpan());
            Assert(acknowledgement.TransactionId == "OFFLINE0001"
                && acknowledgement.GatewayTimestamp == new DateTimeOffset(2026, 10, 6, 15, 0, 0, TimeSpan.Zero)
                && acknowledgement.Errors.Count == 0,
                "The published acknowledgement fields were not parsed into the typed contract.");
        }

        var observedTestReceiptBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
              <EnvelopeVersion>1.0</EnvelopeVersion>
              <Header><MessageDetails><Class>Accounts</Class><Qualifier>response</Qualifier><TransactionID>2026100600000011</TransactionID><GatewayTest>1</GatewayTest><GatewayTimestamp>2026-10-06T19:09:58+00:00</GatewayTimestamp></MessageDetails></Header>
              <GovTalkDetails><Keys/></GovTalkDetails><Body/>
            </GovTalkMessage>
            """);
        using (var client = new HttpClient(new CompaniesHouseRecordingHandler(HttpStatusCode.OK,
                   observedTestReceiptBytes)))
        {
            var response = await new CompaniesHouseOfficialTestHttpGateway(client).ExchangeAsync(new(
                CompaniesHouseGatewayOperation.FormSubmission, ImmutableArray.CreateRange(requestBytes)));
            var acknowledgement = new CompaniesHouseEnvelopeSerializer()
                .ParseGatewayAcknowledgement(response.Content.AsSpan());
            Assert(response.Kind == CompaniesHouseGatewayResponseKind.Acknowledgement
                && acknowledgement.TransactionId == "2026100600000011"
                && acknowledgement.GatewayTimestamp == new DateTimeOffset(2026, 10, 6, 19, 9, 58,
                    TimeSpan.Zero),
                "The observed official-test Accounts/response receipt was not classified as an acknowledgement.");
        }

        var statusBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
              <EnvelopeVersion>1.0</EnvelopeVersion>
              <Header><MessageDetails><Class>GetSubmissionStatus</Class><Qualifier>response</Qualifier><TransactionID>OFFLINE0002</TransactionID></MessageDetails></Header>
              <GovTalkDetails/><Body><SubmissionStatus xmlns="http://xmlgw.companieshouse.gov.uk"><Status><SubmissionNumber>S00001</SubmissionNumber><StatusCode>PENDING</StatusCode><CompanyNumber>01234567</CompanyNumber></Status></SubmissionStatus></Body>
            </GovTalkMessage>
            """);
        using (var client = new HttpClient(new CompaniesHouseRecordingHandler(HttpStatusCode.OK, statusBytes)))
        {
            var response = await new CompaniesHouseOfficialTestHttpGateway(client).ExchangeAsync(new(
                CompaniesHouseGatewayOperation.GetSubmissionStatus, ImmutableArray.CreateRange(requestBytes)));
            var statuses = new CompaniesHouseEnvelopeSerializer().ParseSubmissionStatuses(response.Content.AsSpan());
            Assert(response.Kind == CompaniesHouseGatewayResponseKind.SubmissionStatus
                && statuses.Count == 1 && statuses[0].SubmissionNumber == "S00001"
                && statuses[0].StatusCode == CompaniesHouseSubmissionStatusCode.Pending,
                "The offline HTTP gateway did not validate the published status response contract.");
        }

        var statusAcknowledgementBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
              <EnvelopeVersion>1.0</EnvelopeVersion>
              <Header><MessageDetails><Class>StatusAck</Class><Qualifier>acknowledgement</Qualifier><TransactionID>OFFLINE0004</TransactionID></MessageDetails></Header>
              <GovTalkDetails/><Body/>
            </GovTalkMessage>
            """);
        using (var client = new HttpClient(new CompaniesHouseRecordingHandler(HttpStatusCode.OK,
                   statusAcknowledgementBytes)))
        {
            var response = await new CompaniesHouseOfficialTestHttpGateway(client).ExchangeAsync(new(
                CompaniesHouseGatewayOperation.StatusAck, ImmutableArray.CreateRange(requestBytes)));
            Assert(response.Kind == CompaniesHouseGatewayResponseKind.StatusAcknowledgement,
                "A StatusAck acknowledgement was not classified separately.");
        }

        var errorBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/schemas/govtalk/govtalkheader">
              <EnvelopeVersion>1.0</EnvelopeVersion>
              <Header><MessageDetails><Class>Accounts</Class><Qualifier>error</Qualifier><TransactionID>OFFLINE0003</TransactionID><GatewayTimestamp>2026-10-06T15:01:00+00:00</GatewayTimestamp></MessageDetails></Header>
              <GovTalkDetails><GovTalkErrors><Error><RaisedBy>CH_XML_Gateway</RaisedBy><Number>9999</Number><Type>fatal</Type><Text>Representative offline gateway error</Text><Location/></Error></GovTalkErrors></GovTalkDetails><Body/>
            </GovTalkMessage>
            """);
        using (var client = new HttpClient(new CompaniesHouseRecordingHandler(HttpStatusCode.OK, errorBytes)))
        {
            var response = await new CompaniesHouseOfficialTestHttpGateway(client).ExchangeAsync(new(
                CompaniesHouseGatewayOperation.FormSubmission, ImmutableArray.CreateRange(requestBytes)));
            var parsed = new CompaniesHouseEnvelopeSerializer().ParseGatewayAcknowledgement(response.Content.AsSpan());
            Assert(response.Kind == CompaniesHouseGatewayResponseKind.GatewayError
                && parsed.Errors.Count == 1 && parsed.Errors[0].Code == "9999"
                && parsed.Errors[0].Message == "Representative offline gateway error",
                "The alternate published GovTalk error namespace was not parsed fail-closed.");
        }

        var unclassifiedBytes = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="utf-8"?>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
              <EnvelopeVersion>1.0</EnvelopeVersion>
              <Header><MessageDetails><Class>Accounts</Class><Qualifier>poll</Qualifier><TransactionID>OFFLINE0005</TransactionID></MessageDetails></Header>
              <GovTalkDetails/><Body/>
            </GovTalkMessage>
            """);
        using (var client = new HttpClient(new CompaniesHouseRecordingHandler(HttpStatusCode.OK,
                   unclassifiedBytes)))
        {
            try
            {
                await new CompaniesHouseOfficialTestHttpGateway(client).ExchangeAsync(new(
                    CompaniesHouseGatewayOperation.FormSubmission, ImmutableArray.CreateRange(requestBytes)));
                throw new InvalidOperationException("An unclassified Companies House response was accepted.");
            }
            catch (CompaniesHouseUnclassifiedResponseException exception)
            {
                Assert(exception.ResponseContent.AsSpan().SequenceEqual(unclassifiedBytes)
                    && exception.ResponseSha256 == Hash(unclassifiedBytes),
                    "The unclassified Companies House response was not retained exactly.");
            }
        }

        Assert(CompaniesHouseGatewayEndpoints.IsOfficialTest(CompaniesHouseGatewayEndpoints.OfficialTest)
            && !CompaniesHouseGatewayEndpoints.IsOfficialTest(new("https://evil.example/v1-0/xmlgw/Gateway"))
            && !CompaniesHouseGatewayEndpoints.IsOfficialTest(new(
                "https://xmlgw.companieshouse.gov.uk/v1-0/xmlgw/Gateway?redirect=evil")),
            "The Companies House endpoint allowlist accepts an unpinned host or URI variant.");
        Assert(!typeof(CompaniesHouseOfficialTestHttpGateway).IsPublic
            && typeof(CompaniesHouseOfficialTestHttpGateway).GetConstructors().Length == 0,
            "The offline-only Companies House HTTP gateway is publicly constructible.");

        using (var client = new HttpClient(new CompaniesHouseRecordingHandler(HttpStatusCode.OK, new byte[65])))
        {
            try
            {
                await new CompaniesHouseOfficialTestHttpGateway(client,
                    new CompaniesHouseHttpGatewayOptions(MaximumResponseBytes: 64))
                    .ExchangeAsync(new(CompaniesHouseGatewayOperation.FormSubmission,
                        ImmutableArray.CreateRange(requestBytes)));
                throw new InvalidOperationException("An oversized Companies House response was accepted.");
            }
            catch (InvalidDataException exception) when (exception.Message.Contains("exceeds", StringComparison.Ordinal))
            {
                assertions++;
            }
        }
        var hostile = Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?><!DOCTYPE x [<!ENTITY secret SYSTEM "file:///forbidden">]>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope"><Header><MessageDetails><Class>Accounts</Class><Qualifier>acknowledgement</Qualifier></MessageDetails></Header><Body>&secret;</Body></GovTalkMessage>
            """);
        using (var client = new HttpClient(new CompaniesHouseRecordingHandler(HttpStatusCode.OK, hostile)))
        {
            try
            {
                await new CompaniesHouseOfficialTestHttpGateway(client).ExchangeAsync(new(
                    CompaniesHouseGatewayOperation.FormSubmission, ImmutableArray.CreateRange(requestBytes)));
                throw new InvalidOperationException("A response containing a DTD was accepted.");
            }
            catch (CompaniesHouseUnclassifiedResponseException exception)
                when (exception.InnerException is System.Xml.XmlException
                      && exception.ResponseContent.AsSpan().SequenceEqual(hostile))
            {
                assertions++;
            }
        }
        return assertions;
    }

    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));
}

internal sealed class CompaniesHouseRecordingHandler(HttpStatusCode statusCode, byte[] responseContent)
    : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public byte[]? Content { get; private set; }
    public string? ContentType { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Request = request;
        Content = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        ContentType = request.Content?.Headers.ContentType?.ToString();
        return new HttpResponseMessage(statusCode)
        {
            RequestMessage = request,
            Content = new ByteArrayContent(responseContent)
        };
    }
}
