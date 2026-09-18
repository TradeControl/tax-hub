using System.Security.Cryptography;
using System.Text;
using System.Reflection;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.Shared;
using TradeControl.Tax.UK.Hmrc.Vat;

internal static class PreparedArtifactTests
{
    public static void Run()
    {
        var original = Encoding.UTF8.GetBytes("{\"period\":\"2026\"}");
        var artifact = PreparedStatutoryArtifact.Create(
            "GB", "HMRC", "VAT-RETURN", "v1.0", PreparedArtifactStatus.Preview,
            "application/json", original,
            [new SourceVersion("Cash.fnTaxVatSummary", "0000000000000001", null)]);

        original[0] = 0;
        Assert(Encoding.UTF8.GetString(artifact.Content.AsSpan()) == "{\"period\":\"2026\"}",
            "Prepared bytes retained a mutable caller-owned buffer.");
        Assert(artifact.Sha256 == Convert.ToHexString(SHA256.HashData(artifact.Content.AsSpan())),
            "The digest was not calculated over the final stored bytes.");

        PreparedApiRequestMechanics();

        var document = PreparedStatutoryArtifact.Create(
            "GB", "COMPANIES-HOUSE", "STATUTORY-ACCOUNTS", "TIS5.9",
            PreparedArtifactStatus.SubmissionReady, "application/xhtml+xml",
            Encoding.UTF8.GetBytes("<html>accounts</html>"));
        var envelope = PreparedStatutoryArtifact.Create(
            "GB", "COMPANIES-HOUSE", "FILE-ACCOUNTS", "TIS5.9",
            PreparedArtifactStatus.SubmissionReady, "application/xml",
            Encoding.UTF8.GetBytes("<envelope>encoded-document</envelope>"));
        var package = new PreparedSubmissionPackage(
            "COMPANIES-HOUSE-XML-GATEWAY", envelope,
            [new("accounts.xhtml", document)],
            new(SubmissionPollingMode.PollUntilTerminal, "/submissions/{submissionId}"));

        Assert(!package.Transmission.Content.AsSpan().SequenceEqual(package.Documents[0].Artifact.Content.AsSpan()),
            "The transmitted envelope was conflated with its inspectable document.");
    }

    private static void PreparedApiRequestMechanics()
    {
        var pipeline = new PreparedApiRequestPipeline();
        var vatDescriptor = VatOperationCatalog.All.Single(item => item.OperationId == "vat.returns.submit");
        var body = Encoding.UTF8.GetBytes("{\"finalised\":true}");
        var request = pipeline.Prepare(HmrcPreparedApiContracts.From(vatDescriptor),
            [new("vrn", "123 456/789")], serializeBody: () => body,
            sourceEvidence: [new("TradeControl", "Cash.vwTaxVatSubmission", "0x01")],
            validationStages: [new("source", () => [])]);
        body[0] = 0;
        Assert(request.OperationId == "vat.returns.submit" && request.Method == "POST"
            && request.RelativePath == "/organisations/vat/123%20456%2F789/returns"
            && request.Headers.Select(item => item.Name).SequenceEqual(["Accept", "Content-Type"]),
            "Prepared VAT request metadata, escaping or contract headers are incorrect.");
        Assert(request.BodyBytes.HasValue
            && Encoding.UTF8.GetString(request.BodyBytes.Value.AsSpan()) == "{\"finalised\":true}"
            && request.BodySha256 == Convert.ToHexString(SHA256.HashData(request.BodyBytes.Value.AsSpan())),
            "Prepared request bytes are mutable or its digest was not calculated over stored bytes.");

        var obligations = VatOperationCatalog.All.Single(item => item.OperationId == "vat.obligations.list");
        var bodyless = pipeline.Prepare(HmrcPreparedApiContracts.From(obligations), [new("vrn", "123456789")],
            [new("status", "O"), new("to", "2026-06-30"), new("from", "2026-04-01")]);
        Assert(!bodyless.HasBody && bodyless.BodySha256 is null && bodyless.ContentType is null
            && bodyless.Query.Select(item => item.Name).SequenceEqual(["from", "to", "status"]),
            "Bodyless or ordered-query mechanics are incorrect.");

        var cumulative = SaOperationCatalog.Coverage.Single(item =>
            ReferenceEquals(item.Descriptor, TradeControl.Tax.UK.Hmrc.MtdIncomeTax.v1_0.SelfEmployment.V5.Cumulative.CumulativeEndpoints.Put));
        var blocked = pipeline.Prepare(HmrcPreparedApiContracts.From(cumulative),
            [new("nino", "AA123456A"), new("businessId", "XAIS123"), new("taxYear", "2026-27")],
            serializeBody: () => throw new InvalidOperationException("Serialization must not run after validation failure."),
            validationStages: [new("readiness", () => [new(PreparedFindingSeverity.Error, "NOT-READY", "Source is not ready.")])]);
        Assert(blocked.HasErrors && !blocked.HasBody && blocked.BodySha256 is null,
            "Blocking validation did not suppress body serialization and digest creation.");

        AssertRejected(() => pipeline.Prepare(HmrcPreparedApiContracts.From(vatDescriptor), [], serializeBody: () => []),
            "A missing path value was accepted.");
        AssertRejected(() => pipeline.Prepare(HmrcPreparedApiContracts.From(obligations),
                [new("vrn", "1"), new("vrn", "2")]),
            "A duplicate path value was accepted.");
        AssertRejected(() => pipeline.Prepare(HmrcPreparedApiContracts.From(obligations),
                [new("vrn", "1")], [new("unknown", "value")]),
            "An unknown query value was accepted.");
        AssertRejected(() => pipeline.Prepare(HmrcPreparedApiContracts.From(obligations),
                [new("vrn", "1")], serializeBody: () => Encoding.UTF8.GetBytes("{}")),
            "A bodyless operation accepted invented body bytes.");

        var gateway = new FakeGateway();
        gateway.SendAsync(request).GetAwaiter().GetResult();
        Assert(ReferenceEquals(gateway.Received, request),
            "The gateway did not receive the prepared request unchanged.");

        var forbidden = new[] { "BaseAddress", "OAuth", "Token", "ConnectionString", "Fraud", "Response" };
        var publicMembers = typeof(PreparedApiRequest).GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Select(member => member.Name).ToArray();
        Assert(forbidden.All(term => publicMembers.All(name => !name.Contains(term, StringComparison.OrdinalIgnoreCase))),
            "Prepared API requests expose a forbidden transport, credential, source-connection or response concern.");
    }

    private sealed class FakeGateway : IPreparedApiRequestGateway
    {
        public PreparedApiRequest? Received { get; private set; }
        public Task SendAsync(PreparedApiRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Received = request;
            return Task.CompletedTask;
        }
    }

    private static void AssertRejected(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException(message);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
