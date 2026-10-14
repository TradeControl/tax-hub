using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Authorization;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.WebHarness.Controllers;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

internal static class CorporationTaxValidationDiagnosticsTests
{
    public static async Task<int> RunAsync()
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        const string serviceXml = """
            <?xml version="1.0" encoding="utf-8"?><IRenvelope xmlns="http://www.govtalk.gov.uk/taxation/CT/5"><IRheader><Keys><Key Type="UTR">1234567890</Key></Keys><IRmark Type="generic"></IRmark></IRheader><CompanyTaxReturn ReturnType="new"/></IRenvelope>
            """;
        const string responseXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <GovTalkMessage xmlns="http://www.govtalk.gov.uk/CM/envelope">
              <GovTalkDetails><GovTalkErrors><Error><RaisedBy>ChRIS</RaisedBy><Number>3001</Number><Type>business</Type><Text>Validation failed.</Text></Error></GovTalkErrors></GovTalkDetails>
              <Body><ErrorResponse xmlns="http://www.govtalk.gov.uk/CM/errorresponse"><Error><RaisedBy>ChRIS</RaisedBy><Number>2022</Number><Type>business</Type><Text>IRmark not found.</Text><Location>IRmark</Location></Error></ErrorResponse></Body>
            </GovTalkMessage>
            """;
        var artifact = PreparedStatutoryArtifact.Create("GB", "HMRC", "HMRC-CT600-SERVICE-ROOT",
            "2014-v1.994", PreparedArtifactStatus.Preview, "application/xml", Encoding.UTF8.GetBytes(serviceXml));
        var handler = new RecordingHandler(Encoding.UTF8.GetBytes(responseXml));
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"tax-hub-ct-validation-{Guid.NewGuid():N}");
        try
        {
            var diagnostics = new CorporationTaxValidationDiagnostics(new HttpClient(handler), new()
            {
                LtsEnabled = true,
                TpvsEnabled = true,
                LtsEndpoint = "http://127.0.0.1:5665/LTS/LTSPostServlet",
                TpvsEndpoint = "https://www.tpvs.hmrc.gov.uk/HMRC/CT600"
            }, evidenceRoot, new FixedTimeProvider(new DateTimeOffset(2026, 10, 13, 12, 0, 0, TimeSpan.Zero)));

            var lts = await diagnostics.ValidateLtsAsync(artifact);
            Assert(!lts.IsExternalHmrcResponse
                   && lts.EvidenceClassification == CorporationTaxValidationDiagnostics.LtsClassification,
                "The LTS result was not visibly classified as local evidence.");
            Assert(lts.ServiceArtifactSha256 == artifact.Sha256
                   && lts.RequestBodySha256 == Hash(handler.RequestBodies[0])
                   && lts.ResponseBodySha256 == Hash(Encoding.UTF8.GetBytes(responseXml)),
                "The LTS evidence digests do not identify the exact artifact, request and response bytes.");
            Assert(lts.Findings.Count == 2 && lts.Findings[1].Number == "2022"
                   && lts.Findings[1].Location == "IRmark",
                "The LTS validation errors were not returned as diagnostic findings.");
            Assert(File.ReadAllBytes(Path.Combine(lts.EvidenceDirectory, "service-artifact.xml"))
                       .SequenceEqual(artifact.Content)
                   && Hash(File.ReadAllBytes(Path.Combine(lts.EvidenceDirectory, "request.xml")))
                   == lts.RequestBodySha256
                   && Hash(File.ReadAllBytes(Path.Combine(lts.EvidenceDirectory, "response.bin")))
                   == lts.ResponseBodySha256,
                "The exact LTS request/response evidence was not retained.");
            var requestDocument = XDocument.Parse(lts.ExactRequestBody);
            Assert(requestDocument.Descendants().Any(element => element.Name.LocalName == "Class"
                       && element.Value == "HMRC-CT-CT600")
                   && requestDocument.Descendants().Any(element => element.Name.LocalName == "IRenvelope")
                   && requestDocument.Descendants().Any(element => element.Name.LocalName == "Key"
                       && (string?)element.Attribute("Type") == "UTR" && element.Value == "1234567890"),
                "The diagnostic wrapper does not carry the frozen CT600 service root and required CT class.");

            var tpvs = await diagnostics.ValidateTpvsAsync(artifact);
            Assert(tpvs.IsExternalHmrcResponse
                   && tpvs.EvidenceClassification == CorporationTaxValidationDiagnostics.TpvsClassification
                   && tpvs.Endpoint == "https://www.tpvs.hmrc.gov.uk/HMRC/CT600",
                "The TPVS result was not visibly classified as a genuine remote HMRC response.");
            Assert(handler.RequestUris.SequenceEqual([
                    "http://127.0.0.1:5665/LTS/LTSPostServlet",
                    "https://www.tpvs.hmrc.gov.uk/HMRC/CT600"
                ]) && handler.ContentTypes.All(value => value.StartsWith("application/xml", StringComparison.Ordinal)),
                "The diagnostics did not use the official LTS/TPVS HTTP mechanisms.");
            Assert(handler.RequestBodies[0].SequenceEqual(handler.RequestBodies[1]),
                "LTS and TPVS did not receive the same immutable-artifact diagnostic envelope.");
        }
        finally
        {
            if (Directory.Exists(evidenceRoot)) Directory.Delete(evidenceRoot, true);
        }

        var auth = typeof(CorporationTaxValidationController).GetCustomAttributes(typeof(AuthorizeAttribute), true)
            .Cast<AuthorizeAttribute>().SingleOrDefault();
        Assert(auth?.AuthenticationSchemes == WebHarnessAuthenticationOptions.ApplicationScheme,
            "The Corporation Tax validation operations are not protected by WebHarness authentication.");
        Assert(typeof(CorporationTaxValidationController).GetMethods()
                .Any(method => method.Name == nameof(CorporationTaxValidationController.ValidateTpvs)
                    && method.GetParameters().Any(parameter => parameter.Name == "confirmExternalSend")),
            "The external TPVS operation does not require explicit call-site confirmation.");

        var disabled = new DisabledCorporationTaxValidationDiagnostics();
        await AssertThrowsAsync<InvalidOperationException>(() => disabled.ValidateLtsAsync(artifact),
            "Production-disabled CT validation diagnostics accepted an LTS call.");
        assertions++;
        AssertThrows<ArgumentException>(() => new CorporationTaxValidationDiagnostics(
                new HttpClient(new RecordingHandler([])), new()
                {
                    LtsEnabled = true,
                    LtsEndpoint = "http://example.test/LTS/LTSPostServlet"
                }, Path.GetTempPath()),
            "The LTS client accepted a non-loopback endpoint.");
        assertions++;
        AssertThrows<ArgumentException>(() => new CorporationTaxValidationDiagnostics(
                new HttpClient(new RecordingHandler([])), new()
                {
                    TpvsEnabled = true,
                    TpvsEndpoint = "https://example.test/HMRC/CT600"
                }, Path.GetTempPath()),
            "The TPVS client accepted a non-HMRC endpoint.");
        assertions++;

        return assertions;
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static void AssertThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private sealed class RecordingHandler(byte[] responseBytes) : HttpMessageHandler
    {
        public List<byte[]> RequestBodies { get; } = [];
        public List<string> RequestUris { get; } = [];
        public List<string> ContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.AbsoluteUri);
            ContentTypes.Add(request.Content!.Headers.ContentType!.ToString());
            RequestBodies.Add(await request.Content.ReadAsByteArrayAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes)
                {
                    Headers = { ContentType = new("application/xml") { CharSet = "utf-8" } }
                }
            };
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
