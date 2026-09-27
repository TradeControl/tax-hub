using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.Rest;

public sealed record HmrcPreparedApiRequestGatewayOptions(
    int MaximumResponseBytes = 262_144,
    int MaximumEnquiryAttempts = 2,
    TimeSpan? RequestTimeout = null)
{
    internal TimeSpan EffectiveRequestTimeout => RequestTimeout ?? TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (MaximumResponseBytes is < 1 or > 1_048_576)
            throw new ArgumentException("The authority-response bound is invalid.");
        if (MaximumEnquiryAttempts is < 1 or > 3)
            throw new ArgumentException("The enquiry retry bound is invalid.");
        if (EffectiveRequestTimeout <= TimeSpan.Zero || EffectiveRequestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentException("The authority request timeout is invalid.");
    }
}

public sealed class HmrcRequestNotSentException(string message, Exception? innerException = null)
    : HttpRequestException(message, innerException);

/// <summary>Dispatches only the explicitly enabled prepared HMRC operations to the closed environment profile.</summary>
public sealed class HmrcPreparedApiRequestGateway : PreparedApiRequestGateway, IDisposable
{
    private static readonly JsonSerializerOptions ResponseJson = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly EnvironmentSelector _environment;
    private readonly HmrcOAuthService _oauth;
    private readonly FraudHeaderService _fraud;
    private readonly ISubmissionAttemptStore _attempts;
    private readonly ISubmissionContentStore _content;
    private readonly HttpClient _http;
    private readonly HmrcPreparedApiRequestGatewayOptions _options;
    private readonly bool _ownsHttp;

    public HmrcPreparedApiRequestGateway(EnvironmentSelector environment, HmrcOAuthService oauth,
        FraudHeaderService fraud, ISubmissionAttemptStore attempts, ISubmissionContentStore content,
        HmrcPreparedApiRequestGatewayOptions? options = null)
        : this(environment, oauth, fraud, attempts, content, CreateHttpClient(options), options, true) { }

    internal HmrcPreparedApiRequestGateway(EnvironmentSelector environment, HmrcOAuthService oauth,
        FraudHeaderService fraud, ISubmissionAttemptStore attempts, ISubmissionContentStore content,
        HttpClient http, HmrcPreparedApiRequestGatewayOptions? options = null)
        : this(environment, oauth, fraud, attempts, content, http, options, false) { }

    private HmrcPreparedApiRequestGateway(EnvironmentSelector environment, HmrcOAuthService oauth,
        FraudHeaderService fraud, ISubmissionAttemptStore attempts, ISubmissionContentStore content,
        HttpClient http, HmrcPreparedApiRequestGatewayOptions? options, bool ownsHttp)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _fraud = fraud ?? throw new ArgumentNullException(nameof(fraud));
        _attempts = attempts ?? throw new ArgumentNullException(nameof(attempts));
        _content = content ?? throw new ArgumentNullException(nameof(content));
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? new();
        _options.Validate();
        _ownsHttp = ownsHttp;
        if (!_environment.Selected.LiveRequestsEnabled)
            throw new InvalidOperationException("The selected authority environment is not enabled for live requests.");
    }

    protected override async Task<PreparedApiOutcome> SendEligibleAsync(PreparedApiRequest request,
        AuthorityDispatchContext context, CancellationToken cancellationToken)
    {
        var isWrite = EnsureEnabledOperation(request);
        if (isWrite && (context.LogicalSubmissionReference is null || context.SubjectPeriodReference is null))
            throw new InvalidOperationException(
                "An approved VAT write requires logical-submission and subject-period references.");
        var logicalReference = isWrite
            ? context.LogicalSubmissionReference!
            : Fingerprint($"{request.OperationId}\n{request.RelativePath}\n{QueryString(request)}");
        var subjectPeriodReference = isWrite ? context.SubjectPeriodReference! : logicalReference;
        var attempt = await _attempts.ReserveAsync(new(logicalReference, request.OperationId, request.Method,
            context.TenantReference, context.AuthorisationPrincipalReference, subjectPeriodReference,
            isWrite ? request.BodySha256 : null, isWrite ? context.ApprovalReference : null,
            _environment.Selected.Environment), cancellationToken);

        if (isWrite)
        {
            var payloadReference = await _content.StoreAsync(context.TenantReference,
                context.AuthorisationPrincipalReference, SubmissionContentKind.Payload,
                request.BodyBytes!.Value.AsMemory(), cancellationToken);
            attempt = await _attempts.RecordPayloadAsync(context.TenantReference,
                context.AuthorisationPrincipalReference, attempt.AttemptReference, payloadReference,
                cancellationToken);
        }

        using var access = await _oauth.GetAccessAsync(context, request.RequiredOAuthScope, cancellationToken);
        if (access.Kind != OAuthAccessOutcomeKind.Available || access.AccessToken is null)
            return await FinishAsync(attempt, PreparedApiOutcomeKind.Rejected,
                $"OAUTH-REAUTHORISATION-{access.ReauthorisationReason?.ToString().ToUpperInvariant() ?? "REQUIRED"}",
                null, null, null, cancellationToken);

        FraudPreventionHeaders fraudHeaders;
        try { fraudHeaders = await _fraud.BuildHeadersAsync(context, cancellationToken); }
        catch (FraudContextRejectedException)
        {
            return await FinishAsync(attempt, PreparedApiOutcomeKind.Failed, "FRAUD-CONTEXT-REJECTED",
                null, null, null, cancellationToken);
        }

        await _attempts.RecordOutcomeAsync(context.TenantReference, context.AuthorisationPrincipalReference,
            attempt.AttemptReference, new(SubmissionAttemptState.Sending, "SENDING"), cancellationToken);

        var token = Copy(access.AccessToken);
        try
        {
            var maximumAttempts = isWrite ? 1 : _options.MaximumEnquiryAttempts;
            for (var send = 1; send <= maximumAttempts; send++)
            {
                try
                {
                    using var message = CreateRequest(request, token, fraudHeaders);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(_options.EffectiveRequestTimeout);
                    using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token);
                    var bytes = await ReadBoundedAsync(response.Content, timeout.Token);
                    var responseReference = bytes.Length == 0 ? null : await _content.StoreAsync(
                        context.TenantReference, context.AuthorisationPrincipalReference,
                        SubmissionContentKind.Response, bytes, cancellationToken);
                    var correlation = Correlation(response);

                    if (!isWrite && send < maximumAttempts && IsRetryable(response.StatusCode)) continue;
                    return await ClassifyAsync(request, attempt, response.StatusCode, bytes,
                        responseReference, correlation, context, cancellationToken);
                }
                catch (ResponseTooLargeException)
                {
                    return await FinishAsync(attempt, PreparedApiOutcomeKind.Failed, "HMRC-RESPONSE-TOO-LARGE",
                        null, null, null, cancellationToken);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!isWrite && send < maximumAttempts) continue;
                    return await FinishAsync(attempt, PreparedApiOutcomeKind.Unknown,
                        isWrite ? "HMRC-WRITE-OUTCOME-UNKNOWN" : "HMRC-TIMEOUT",
                        null, null, null, cancellationToken);
                }
                catch (HmrcRequestNotSentException)
                {
                    return await FinishAsync(attempt, PreparedApiOutcomeKind.Failed, "HMRC-REQUEST-NOT-SENT",
                        null, null, null, cancellationToken);
                }
                catch (HttpRequestException)
                {
                    if (!isWrite && send < maximumAttempts) continue;
                    return await FinishAsync(attempt, PreparedApiOutcomeKind.Unknown,
                        isWrite ? "HMRC-WRITE-OUTCOME-UNKNOWN" : "HMRC-TRANSPORT-UNKNOWN",
                        null, null, null, cancellationToken);
                }
            }
            throw new InvalidOperationException("The bounded enquiry loop did not terminate.");
        }
        finally { token = string.Empty; }
    }

    private async Task<PreparedApiOutcome> ClassifyAsync(PreparedApiRequest request,
        SubmissionAttemptRecord attempt, HttpStatusCode status, byte[] bytes, string? responseReference,
        string? correlation, AuthorityDispatchContext context, CancellationToken cancellationToken)
    {
        var actual = (int)status;
        if (actual == request.ExpectedSuccessStatusCode)
        {
            if (bytes.Length == 0)
                return await FinishAsync(attempt, PreparedApiOutcomeKind.Failed, "HMRC-RESPONSE-EMPTY",
                    actual, responseReference, correlation, cancellationToken);
            try
            {
                var result = JsonSerializer.Deserialize(bytes, request.ExpectedResponseType!, ResponseJson);
                if (result is null) throw new JsonException();
            }
            catch (JsonException)
            {
                return await FinishAsync(attempt, PreparedApiOutcomeKind.Failed, "HMRC-RESPONSE-MALFORMED",
                    actual, responseReference, correlation, cancellationToken);
            }
            SubmissionReceiptEvidence? receipt = null;
            if (request.OperationId == "vat.returns.submit")
                receipt = Receipt(bytes);
            return await FinishAsync(attempt, PreparedApiOutcomeKind.Succeeded, "HMRC-SUCCESS",
                actual, responseReference, correlation, cancellationToken, receipt);
        }

        var code = SafeHmrcErrorCode(bytes, actual);
        var kind = actual is >= 400 and < 500 && actual != 429
            ? PreparedApiOutcomeKind.Rejected : PreparedApiOutcomeKind.Failed;
        return await FinishAsync(attempt, kind, code, actual, responseReference, correlation, cancellationToken);
    }

    private async Task<PreparedApiOutcome> FinishAsync(SubmissionAttemptRecord attempt, PreparedApiOutcomeKind kind,
        string code, int? status, string? responseReference, string? correlation,
        CancellationToken cancellationToken, SubmissionReceiptEvidence? receipt = null)
    {
        var state = kind switch
        {
            PreparedApiOutcomeKind.Succeeded => SubmissionAttemptState.Succeeded,
            PreparedApiOutcomeKind.Rejected => SubmissionAttemptState.Rejected,
            PreparedApiOutcomeKind.Failed => SubmissionAttemptState.Failed,
            _ => SubmissionAttemptState.Unknown
        };
        await _attempts.RecordOutcomeAsync(attempt.TenantReference, attempt.PrincipalReference,
            attempt.AttemptReference, new(state, code, status, correlation, responseReference, receipt), cancellationToken);
        return new(kind, code, status, attempt.AttemptReference, responseReference);
    }

    private HttpRequestMessage CreateRequest(PreparedApiRequest request, string token,
        FraudPreventionHeaders fraudHeaders)
    {
        var uri = _environment.ResolveApiPath(request.RelativePath);
        var query = QueryString(request);
        if (query.Length != 0) uri = new UriBuilder(uri) { Query = query }.Uri;
        var message = new HttpRequestMessage(new HttpMethod(request.Method), uri);
        if (request.HasBody)
        {
            message.Content = new ByteArrayContent(request.BodyBytes!.Value.ToArray());
            if (request.ContentType is not null)
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        }
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        foreach (var header in request.Headers)
        {
            if (header.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) continue;
            if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value))
                throw new InvalidOperationException("A prepared request header was invalid.");
        }
        foreach (var header in fraudHeaders)
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
                throw new InvalidOperationException("A fraud-prevention header was invalid.");
        return message;
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > _options.MaximumResponseBytes) throw new ResponseTooLargeException();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > _options.MaximumResponseBytes) throw new ResponseTooLargeException();
            await buffer.WriteAsync(block.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool EnsureEnabledOperation(PreparedApiRequest request)
    {
        var enquiry = request.Method == "GET" && !request.HasBody
            && request.OperationId is ("vat.obligations.list" or "vat.returns.retrieve");
        var vatWrite = request.Method == "POST" && request.HasBody
            && request.OperationId == "vat.returns.submit" && request.BodySha256 is not null;
        if ((!enquiry && !vatWrite)
            || request.ResponseBodyExpectation != PreparedApiResponseBodyExpectation.Json
            || request.ExpectedResponseType is null)
            throw new InvalidOperationException("Only the approved Phase 5.4/5.5 VAT operations can be dispatched.");
        if (vatWrite && (!request.BodyBytes.HasValue || request.BodyBytes.Value.Length == 0))
            throw new InvalidOperationException("The approved VAT write requires prepared canonical bytes.");
        return vatWrite;
    }

    private static string QueryString(PreparedApiRequest request) => string.Join("&", request.Query.Select(item =>
        $"{Uri.EscapeDataString(item.Name)}={Uri.EscapeDataString(item.Value)}"));

    private static bool IsRetryable(HttpStatusCode status) => status == HttpStatusCode.TooManyRequests
        || (int)status is >= 500 and <= 599;

    private static string SafeHmrcErrorCode(byte[] bytes, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("code", out var property))
            {
                var value = property.GetString();
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 96
                    && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-'))
                    return $"HMRC-{value}";
            }
        }
        catch (JsonException) { }
        return $"HMRC-HTTP-{status}";
    }

    private static SubmissionReceiptEvidence Receipt(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString() : null;
        var processing = Text(root, "processingDate");
        return new(DateTimeOffset.TryParse(processing, out var value) ? value : null,
            Text(root, "paymentIndicator"), Text(root, "formBundleNumber"), Text(root, "chargeRefNumber"));
    }

    private static string? Correlation(HttpResponseMessage response)
    {
        foreach (var name in new[] { "CorrelationId", "X-CorrelationId", "X-Correlation-ID" })
            if (response.Headers.TryGetValues(name, out var values))
            {
                var value = values.FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(value) && value.Length <= 128
                    && value.All(character => !char.IsControl(character))) return value;
            }
        return null;
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Copy(ProtectedSecret secret)
    {
        var chars = new char[secret.Length];
        try { secret.CopyTo(chars); return new string(chars); }
        finally { Array.Clear(chars); }
    }

    private static HttpClient CreateHttpClient(HmrcPreparedApiRequestGatewayOptions? options)
    {
        var selected = options ?? new();
        selected.Validate();
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }

    private sealed class ResponseTooLargeException : Exception;
}
