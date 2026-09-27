using System.Net;
using System.Text;
using System.Security.Cryptography;
using System.Web;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;
using TradeControl.Tax.UK.Adapters.Submission.Rest;
using TradeControl.Tax.UK.Application.Preparation;

internal static class RestGatewayTests
{
    public static async Task<int> RunAsync(string root, ISecretProvider secrets)
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        var now = new DateTimeOffset(2026, 9, 26, 18, 0, 0, TimeSpan.Zero);
        var clock = new FixedClock(now);
        var identity = new FraudActorIdentity("tenant-rest", "principal-rest", "actor-rest");
        var oauthContext = new AuthorityDispatchContext(identity.TenantReference, identity.PrincipalReference,
            identity.ActorReference, "approval-rest", new string('A', 64));
        var tokenEndpoint = new TokenEndpoint();
        using var oauth = HmrcOAuthService.CreateFileBackedSandbox(
            Path.Combine(root, "rest-oauth.enc"), Enumerable.Range(1, 32).Select(x => (byte)x).ToArray(),
            secrets, tokenEndpoint, clock);
        var start = await oauth.BeginAuthorisationAsync(oauthContext, HmrcOAuthScopes.ReadVat);
        var state = HttpUtility.ParseQueryString(start.AuthorisationUri.Query)["state"]!;
        using (var access = await oauth.CompleteCallbackAsync(oauthContext, new(state, "rest-code")))
            Assert(access.Kind == OAuthAccessOutcomeKind.Available, "The REST test grant was not established.");
        tokenEndpoint.Scope = HmrcOAuthScopes.WriteVat;
        var writeStart = await oauth.BeginAuthorisationAsync(oauthContext, HmrcOAuthScopes.WriteVat);
        var writeState = HttpUtility.ParseQueryString(writeStart.AuthorisationUri.Query)["state"]!;
        using (var access = await oauth.CompleteCallbackAsync(oauthContext, new(writeState, "write-code")))
            Assert(access.Kind == OAuthAccessOutcomeKind.Available, "The VAT write test grant was not established.");

        var topology = FraudDeploymentTopology.Direct("rest-direct", IPAddress.Parse("8.8.8.8"));
        var vendor = new FraudVendorConfiguration("Trade Control Tax Hub",
            new Dictionary<string, string> { ["tax-hub"] = "5.4.0" },
            new Dictionary<string, string> { ["trade-control"] = new string('F', 64) });
        using var fraud = FraudHeaderService.CreateFileBacked(Path.Combine(root, "rest-fraud"),
            Enumerable.Range(40, 32).Select(x => (byte)x).ToArray(), topology, vendor, clock: clock);
        var sealedFacts = await fraud.CaptureAndSealAsync(identity, new BrowserFraudFacts(
            "Mozilla/5.0 TaxHub/5.4", Guid.Parse("d27a485a-18d6-49f1-a618-18bb54fa8011"),
            [new(FraudMultiFactorType.AuthorisationCode, now.AddMinutes(-1), new string('B', 64))],
            [new(1920, 1080, 1.25m, 24)], "UTC+01:00",
            new Dictionary<string, string> { ["tax-hub"] = "rest-user" }, new(1200, 800)),
            new(IPAddress.Parse("1.1.1.1"), 55123, now));
        var context = new AuthorityDispatchContext(identity.TenantReference, identity.PrincipalReference,
            identity.ActorReference, "approval-rest", sealedFacts.Value);
        var attempts = new FileSubmissionAttemptStore(SubmissionAttemptStoreOptions.SevenYearMetadata(
            Path.Combine(root, "rest-attempts.json")), clock);
        var contents = new FileSubmissionContentStore(new(Path.Combine(root, "rest-responses"),
            MaximumResponseBytes: 512));
        var described = new BodylessRequestDescriber(new PreparedApiRequestPipeline()).Describe(
            new DescribeVatObligations("123456789", new(2026, 4, 1), new(2026, 6, 30), "O"));

        var handler = new RecordingHandler();
        handler.Enqueue(HttpStatusCode.OK, "{\"obligations\":[]}");
        using var gateway = new HmrcPreparedApiRequestGateway(EnvironmentSelector.Sandbox(), oauth, fraud,
            attempts, contents, new HttpClient(handler), new(MaximumResponseBytes: 512,
                MaximumEnquiryAttempts: 2, RequestTimeout: TimeSpan.FromSeconds(2)));
        var success = await gateway.SendAsync(described, context);
        var sent = handler.Requests.Single();
        Assert(success.Kind == PreparedApiOutcomeKind.Succeeded && success.ActualStatusCode == 200
            && success.AttemptReference is not null && success.SafeResponseReference is not null,
            "A valid pinned VAT enquiry response was not accepted and referenced.");
        Assert(sent.Method == HttpMethod.Get && sent.Content is null
            && sent.RequestUri!.AbsoluteUri == "https://test-api.service.hmrc.gov.uk/organisations/vat/123456789/obligations?from=2026-04-01&to=2026-06-30&status=O",
            "The prepared VAT enquiry method, path or ordered query changed in dispatch.");
        Assert(sent.Headers.Authorization?.Scheme == "Bearer"
            && sent.Headers.Authorization.Parameter == "rest-access-token"
            && sent.Headers.Accept.Single().MediaType == "application/vnd.hmrc.1.0+json"
            && sent.Headers.Contains("Gov-Client-Device-ID"),
            "The scoped bearer token, pinned Accept or fresh fraud headers were absent.");
        var storedSuccess = await contents.ReadAsync(identity.TenantReference, identity.PrincipalReference,
            success.SafeResponseReference!);
        Assert(Encoding.UTF8.GetString(storedSuccess!) == "{\"obligations\":[]}",
            "The exact bounded authority response was not retained.");

        handler.Enqueue(HttpStatusCode.OK, "{}");
        var specialReturn = new BodylessRequestDescriber(new PreparedApiRequestPipeline()).Describe(
            new DescribeVatReturn("123456789", "#001"));
        var returnOutcome = await gateway.SendAsync(specialReturn, context);
        Assert(returnOutcome.Kind == PreparedApiOutcomeKind.Succeeded
            && handler.Requests.Last().RequestUri!.AbsoluteUri ==
                "https://test-api.service.hmrc.gov.uk/organisations/vat/123456789/returns/%23001",
            "The documented VAT special period key was not escaped and dispatched exactly.");

        handler.Enqueue(HttpStatusCode.TooManyRequests, "{\"code\":\"RATE_LIMIT\",\"detail\":\"bounded\",\"future\":7}");
        handler.Enqueue(HttpStatusCode.OK, "{\"obligations\":[]}");
        var retried = await gateway.SendAsync(described, context);
        Assert(retried.Kind == PreparedApiOutcomeKind.Succeeded && handler.Requests.Count == 4,
            "A bounded safe 429 enquiry retry was not performed exactly once.");

        handler.Enqueue(HttpStatusCode.BadRequest,
            "{\"code\":\"INVALID_DATE_RANGE\",\"message\":\"No\",\"unknown\":{\"x\":1}}");
        var rejected = await gateway.SendAsync(described, context);
        Assert(rejected.Kind == PreparedApiOutcomeKind.Rejected
            && rejected.OutcomeCode == "HMRC-INVALID_DATE_RANGE" && rejected.ActualStatusCode == 400,
            "A structured HMRC 4xx response was not classified without losing its code.");
        var rawError = await contents.ReadAsync(identity.TenantReference, identity.PrincipalReference,
            rejected.SafeResponseReference!);
        Assert(Encoding.UTF8.GetString(rawError!).Contains("\"unknown\"", StringComparison.Ordinal),
            "Unknown HMRC error fields were not preserved in protected raw evidence.");

        handler.Enqueue(HttpStatusCode.NotFound, "");
        var emptyNotFound = await gateway.SendAsync(specialReturn, context);
        Assert(emptyNotFound.Kind == PreparedApiOutcomeKind.Rejected
            && emptyNotFound.ActualStatusCode == 404
            && emptyNotFound.OutcomeCode == "HMRC-HTTP-404"
            && emptyNotFound.SafeResponseReference is null,
            "An empty HMRC rejection did not retain its actual status classification.");

        handler.Enqueue(HttpStatusCode.OK, "not-json");
        var malformed = await gateway.SendAsync(described, context);
        Assert(malformed.Kind == PreparedApiOutcomeKind.Failed
            && malformed.OutcomeCode == "HMRC-RESPONSE-MALFORMED",
            "Malformed success JSON was not rejected against the pinned DTO.");
        handler.Enqueue(HttpStatusCode.OK, "");
        var empty = await gateway.SendAsync(described, context);
        Assert(empty.Kind == PreparedApiOutcomeKind.Failed && empty.OutcomeCode == "HMRC-RESPONSE-EMPTY",
            "An empty success response was not classified.");
        handler.Enqueue(HttpStatusCode.OK, new string('X', 513));
        var oversized = await gateway.SendAsync(described, context);
        Assert(oversized.Kind == PreparedApiOutcomeKind.Failed
            && oversized.OutcomeCode == "HMRC-RESPONSE-TOO-LARGE",
            "An oversized response crossed the configured bound.");

        handler.EnqueueException(new HttpRequestException("synthetic transport failure"));
        handler.EnqueueException(new HttpRequestException("synthetic transport failure"));
        var unknown = await gateway.SendAsync(described, context);
        Assert(unknown.Kind == PreparedApiOutcomeKind.Unknown && unknown.OutcomeCode == "HMRC-TRANSPORT-UNKNOWN",
            "An exhausted uncertain transport result was not classified as unknown.");

        var writeBytes = Encoding.UTF8.GetBytes(
            "{\"periodKey\":\"18A2\",\"vatDueSales\":100.00,\"vatDueAcquisitions\":0.00,\"totalVatDue\":100.00,\"vatReclaimedCurrPeriod\":20.00,\"netVatDue\":80.00,\"totalValueSalesExVAT\":500,\"totalValuePurchasesExVAT\":100,\"totalValueGoodsSuppliedExVAT\":0,\"totalAcquisitionsExVAT\":0,\"finalised\":true}");
        var write = PreparedWrite(writeBytes);
        var writeContext = new AuthorityDispatchContext(identity.TenantReference, identity.PrincipalReference,
            identity.ActorReference, "approval-reviewed-001", sealedFacts.Value,
            "vat:763197702:18A2", "subject-period:763197702:18A2");
        handler.Enqueue(HttpStatusCode.Created,
            "{\"processingDate\":\"2026-09-26T18:00:00Z\",\"formBundleNumber\":\"123456789012\",\"paymentIndicator\":\"DD\",\"chargeRefNumber\":\"A1B2C3\"}",
            ("X-CorrelationId", "write-correlation-001"));
        var writeSuccess = await gateway.SendAsync(write, writeContext);
        var writeRequest = handler.Requests.Last();
        Assert(writeSuccess.Kind == PreparedApiOutcomeKind.Succeeded && writeSuccess.ActualStatusCode == 201
            && writeRequest.Method == HttpMethod.Post
            && writeRequest.RequestUri!.AbsolutePath == "/organisations/vat/763197702/returns"
            && handler.Bodies.Last()!.SequenceEqual(writeBytes),
            "The VAT write did not send the exact prepared bytes once and accept only 201.");
        Assert(write.BodySha256 == Convert.ToHexString(SHA256.HashData(handler.Bodies.Last()!))
            && writeRequest.Content?.Headers.ContentType?.MediaType == "application/json",
            "The transmitted VAT bytes, digest or prepared content type changed at dispatch.");
        var writeAttempt = await attempts.GetAsync(identity.TenantReference, identity.PrincipalReference,
            writeSuccess.AttemptReference!);
        var retainedPayload = await contents.ReadAsync(identity.TenantReference, identity.PrincipalReference,
            writeAttempt!.SafePayloadReference!);
        Assert(writeAttempt.PreparedDigest == write.BodySha256
            && writeAttempt.ApprovalReference == "approval-reviewed-001"
            && writeAttempt.CorrelationReference == "write-correlation-001"
            && writeAttempt.Receipt?.ProcessingDate == now
            && writeAttempt.Receipt?.FormBundleNumber == "123456789012"
            && writeAttempt.Receipt?.ChargeReference == "A1B2C3"
            && writeAttempt.Receipt?.PaymentIndicator == "DD"
            && retainedPayload!.SequenceEqual(writeBytes),
            "The VAT attempt did not durably link its digest, approval, receipt correlation and protected bytes.");

        var notSentContext = WriteContext(context, sealedFacts.Value, "not-sent");
        handler.EnqueueException(new HmrcRequestNotSentException("synthetic pre-connection failure"));
        var notSent = await gateway.SendAsync(write, notSentContext);
        Assert(notSent.Kind == PreparedApiOutcomeKind.Failed && notSent.OutcomeCode == "HMRC-REQUEST-NOT-SENT",
            "A demonstrably pre-connection VAT failure was not classified as safe failure.");

        var ambiguousContext = WriteContext(context, sealedFacts.Value, "ambiguous");
        handler.EnqueueException(new HttpRequestException("synthetic reset after sending began"));
        var requestCountBeforeUnknown = handler.Requests.Count;
        var ambiguous = await gateway.SendAsync(write, ambiguousContext);
        Assert(ambiguous.Kind == PreparedApiOutcomeKind.Unknown
            && ambiguous.OutcomeCode == "HMRC-WRITE-OUTCOME-UNKNOWN"
            && handler.Requests.Count == requestCountBeforeUnknown + 1,
            "An ambiguous VAT write was retried or not marked reconciliation-required.");
        var blocked = false;
        try { await gateway.SendAsync(write, ambiguousContext); }
        catch (ActiveSubmissionAttemptException) { blocked = true; }
        Assert(blocked && handler.Requests.Count == requestCountBeforeUnknown + 1,
            "An ambiguous VAT write was silently replayed.");
        var restartedAttempts = new FileSubmissionAttemptStore(SubmissionAttemptStoreOptions.SevenYearMetadata(
            Path.Combine(root, "rest-attempts.json")), clock);
        using var restartedGateway = new HmrcPreparedApiRequestGateway(EnvironmentSelector.Sandbox(), oauth, fraud,
            restartedAttempts, contents, new HttpClient(handler), new(MaximumResponseBytes: 512,
                MaximumEnquiryAttempts: 2, RequestTimeout: TimeSpan.FromSeconds(2)));
        blocked = false;
        try { await restartedGateway.SendAsync(write, ambiguousContext); }
        catch (ActiveSubmissionAttemptException) { blocked = true; }
        Assert(blocked, "A restart lost the unresolved VAT write and allowed replay.");

        var missingContext = new AuthorityDispatchContext("tenant-missing", "principal-missing", "actor-missing",
            "approval", new string('A', 64));
        var requestsBefore = handler.Requests.Count;
        var reauthorise = await gateway.SendAsync(described, missingContext);
        Assert(reauthorise.Kind == PreparedApiOutcomeKind.Rejected
            && reauthorise.OutcomeCode.Contains("MISSINGGRANT", StringComparison.Ordinal)
            && handler.Requests.Count == requestsBefore,
            "A missing scoped grant did not return a typed reauthorisation result before transport.");

        var source = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "..", "src", "TradeControl.Tax.UK.Adapters.Submission", "Rest", "HmrcPreparedApiRequestGateway.cs"));
        Assert(!source.Contains("VatJson", StringComparison.Ordinal)
            && !source.Contains("TradeControl.Tax.UK.Hmrc.Vat", StringComparison.Ordinal),
            "The REST adapter bypassed the prepared boundary through a VAT serializer dependency.");
        return assertions;
    }

    private static PreparedApiRequest PreparedWrite(byte[] bytes) =>
        new PreparedApiRequestPipeline().Prepare(new PreparedApiContract(
            "vat.returns.submit", "hmrc-vat", "1.0", false, PreparedApiEligibility.Supported,
            "POST", "/organisations/vat/{vrn}/returns", [new("vrn")], [],
            "application/vnd.hmrc.1.0+json", "application/json", true, HmrcOAuthScopes.WriteVat, 201,
            PreparedApiResponseBodyExpectation.Json, typeof(WriteResponse)),
            [new("vrn", "763197702")], serializeBody: () => bytes);

    private static AuthorityDispatchContext WriteContext(AuthorityDispatchContext context,
        string sealedFacts, string suffix) => new(context.TenantReference,
        context.AuthorisationPrincipalReference, context.ActorReference, $"approval-{suffix}", sealedFacts,
        $"vat:763197702:18A2:{suffix}", $"subject-period:763197702:18A2:{suffix}");

    private sealed class WriteResponse
    {
        public DateTime ProcessingDate { get; set; }
        public string? FormBundleNumber { get; set; }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TokenEndpoint : IHmrcOAuthTokenEndpoint
    {
        public string Scope { get; set; } = HmrcOAuthScopes.ReadVat;
        public Task<OAuthTokenResponse> ExchangeCodeAsync(string clientId, string clientSecret, string code,
            string codeVerifier, Uri redirectUri, CancellationToken cancellationToken = default) =>
            Task.FromResult(new OAuthTokenResponse("rest-access-token", "rest-refresh-token", TimeSpan.FromHours(4),
                new HashSet<string>(StringComparer.Ordinal) { Scope }));
        public Task<OAuthTokenResponse> RefreshAsync(string clientId, string clientSecret, string refreshToken,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Unexpected refresh.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<byte[]?> Bodies { get; } = [];
        public void Enqueue(HttpStatusCode status, string body) => _responses.Enqueue(() =>
            new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        public void Enqueue(HttpStatusCode status, string body, params (string Name, string Value)[] headers) =>
            _responses.Enqueue(() =>
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                foreach (var header in headers) response.Headers.TryAddWithoutValidation(header.Name, header.Value);
                return response;
            });
        public void EnqueueException(Exception exception) => _responses.Enqueue(() => throw exception);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var copy = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            if (request.Content is not null)
            {
                copy.Content = new ByteArrayContent([]);
                foreach (var header in request.Content.Headers)
                    copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            Requests.Add(copy);
            Bodies.Add(request.Content?.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult());
            return Task.FromResult(_responses.Dequeue()());
        }
    }
}
