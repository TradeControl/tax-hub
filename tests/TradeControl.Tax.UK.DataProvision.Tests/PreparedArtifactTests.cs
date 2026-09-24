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
            && request.Eligibility == PreparedApiEligibility.Supported
            && request.RequiredOAuthScope == "write:vat" && request.ExpectedSuccessStatusCode == 201
            && request.ResponseBodyExpectation == PreparedApiResponseBodyExpectation.Json
            && request.ExpectedResponseType == vatDescriptor.ResponseType
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
            && bodyless.RequiredOAuthScope == "read:vat" && bodyless.ExpectedSuccessStatusCode == 200
            && bodyless.ResponseBodyExpectation == PreparedApiResponseBodyExpectation.Json
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
        Assert(blocked.ExpectedSuccessStatusCode == 204
            && blocked.ResponseBodyExpectation == PreparedApiResponseBodyExpectation.None
            && blocked.ExpectedResponseType is null,
            "An MTD 204 success did not retain its empty response expectation.");

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
        var complete = HmrcPreparedApiContracts.From(obligations);
        AssertRejected(() => pipeline.Prepare(complete with { RequiredOAuthScope = "" }, [new("vrn", "1")]),
            "A contract without a scope was accepted.");
        AssertRejected(() => pipeline.Prepare(complete with { ExpectedSuccessStatusCode = 0 }, [new("vrn", "1")]),
            "A contract without an exact success status was accepted.");
        AssertRejected(() => pipeline.Prepare(complete with { ExpectedResponseType = null }, [new("vrn", "1")]),
            "A JSON response contract without a DTO was accepted.");

        var context = new AuthorityDispatchContext("tenant-01", "principal-01", "actor-01", "approval-01", "facts-01");
        AssertRejected(() => _ = new AuthorityDispatchContext("", "principal-01", "actor-01", "approval-01", "facts-01"),
            "A dispatch context without a tenant reference was accepted.");
        Assert(typeof(AuthorityDispatchContext).GetProperties().All(property =>
                !property.Name.Contains("Environment", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("Host", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase)
                && !property.Name.Contains("Token", StringComparison.OrdinalIgnoreCase)),
            "Dispatch context exposes environment selection or credentials.");
        var gateway = new FakeGateway();
        var outcome = gateway.SendAsync(request, context).GetAwaiter().GetResult();
        Assert(ReferenceEquals(gateway.Received, request) && outcome.Kind == PreparedApiOutcomeKind.Succeeded,
            "The gateway did not receive the prepared request unchanged.");

        var deferredDescriptor = VatOperationCatalog.All.Single(item => item.OperationId == "vat.payments.list");
        var deferred = pipeline.Prepare(HmrcPreparedApiContracts.From(deferredDescriptor),
            [new("vrn", "123456789")]);
        Assert(deferred.Eligibility == PreparedApiEligibility.Deferred && !deferred.IsPreview,
            "A deferred VAT operation lost its catalogue classification.");
        AssertRejected(() => gateway.SendAsync(deferred, context).GetAwaiter().GetResult(),
            "A deferred operation crossed the gateway port.");
        var previewCoverage = SaOperationCatalog.Coverage.Single(item => item.Descriptor.Preview);
        var unsupportedContract = HmrcPreparedApiContracts.From(previewCoverage);
        Assert(unsupportedContract.Eligibility == PreparedApiEligibility.Unsupported && unsupportedContract.IsPreview,
            "The unsupported Income Tax preview lost either classification.");
        var unsupported = pipeline.Prepare(unsupportedContract with { IsPreview = false },
            [new("nino", "AA123456A"), new("businessId", "XAIS123"), new("taxYear", "2026-27")],
            serializeBody: () => Encoding.UTF8.GetBytes("{}"));
        AssertRejected(() => gateway.SendAsync(unsupported, context).GetAwaiter().GetResult(),
            "An unsupported operation crossed the gateway port.");
        var previewContract = unsupportedContract with
        {
            Eligibility = PreparedApiEligibility.Supported
        };
        var preview = pipeline.Prepare(previewContract,
            [new("nino", "AA123456A"), new("businessId", "XAIS123"), new("taxYear", "2026-27")],
            serializeBody: () => Encoding.UTF8.GetBytes("{}"));
        AssertRejected(() => gateway.SendAsync(preview, context).GetAwaiter().GetResult(),
            "A preview operation crossed the gateway port.");
        AssertRejected(() => gateway.SendAsync(blocked, context).GetAwaiter().GetResult(),
            "An error-bearing operation crossed the gateway port.");

        var forbidden = new[] { "BaseAddress", "AccessToken", "Bearer", "ClientSecret", "ConnectionString",
            "FraudHeader", "ReceivedResponse", "ActualStatusCode" };
        var publicMembers = typeof(PreparedApiRequest).GetMembers(BindingFlags.Instance | BindingFlags.Public)
            .Select(member => member.Name).ToArray();
        Assert(forbidden.All(term => publicMembers.All(name => !name.Contains(term, StringComparison.OrdinalIgnoreCase))),
            "Prepared API requests expose a forbidden transport, credential, source-connection or response concern.");
        var publicProperties = typeof(PreparedApiRequest).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name).ToArray();
        Assert(publicProperties.Where(name => name.Contains("Response", StringComparison.OrdinalIgnoreCase))
                .All(name => name is nameof(PreparedApiRequest.ResponseBodyExpectation)
                    or nameof(PreparedApiRequest.ExpectedResponseType)),
            "Prepared API requests expose received response state rather than contract expectations.");
    }

    private sealed class FakeGateway : PreparedApiRequestGateway
    {
        public PreparedApiRequest? Received { get; private set; }
        protected override Task<PreparedApiOutcome> SendEligibleAsync(PreparedApiRequest request,
            AuthorityDispatchContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Received = request;
            return Task.FromResult(new PreparedApiOutcome(PreparedApiOutcomeKind.Succeeded, "FAKE-SUCCESS"));
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
