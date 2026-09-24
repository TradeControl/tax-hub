using System.Reflection;
using System.Net;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.Hmrc.Vat;
using TradeControl.Tax.UK.WebHarness.Controllers;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Hmrc;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

PreparedApiRequest Request(string marker)
{
    var descriptor = VatOperationCatalog.All.Single(item => item.OperationId == "vat.returns.submit");
    return new PreparedApiRequestPipeline().Prepare(HmrcPreparedApiContracts.From(descriptor),
        [new("vrn", "123456789")], serializeBody: () => Encoding.UTF8.GetBytes($"{{\"marker\":\"{marker}\"}}"));
}

var time = new MutableTimeProvider(new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero));
var store = new PreparedApiRequestStore(new(2, TimeSpan.FromMinutes(5)), time);
var originalFirst = Request("first");
var first = await store.SaveAsync(originalFirst);
var second = await store.SaveAsync(Request("second"));
Assert(store.TryGet(first, out var firstRequest) && ReferenceEquals(firstRequest, originalFirst),
    "A stored preparation could not be retrieved.");
var thirdRequest = Request("third");
var third = await store.SaveAsync(thirdRequest);
Assert(!store.TryGet(first, out _) && store.TryGet(second, out _) && store.TryGet(third, out var retrieved)
    && ReferenceEquals(retrieved, thirdRequest),
    "The bounded store did not evict only its oldest preparation.");
Assert(!store.TryGet("../../not-an-id", out _) && !store.TryGet(new string('z', 32), out _),
    "The store accepted a non-opaque preparation identifier.");
time.Advance(TimeSpan.FromMinutes(5));
Assert(!store.TryGet(second, out _) && !store.TryGet(third, out _),
    "Expired preparations remained available.");

var request = Request("exact-bytes");
var context = new DefaultHttpContext();
context.Response.Body = new MemoryStream();
await PreparedApiRequestHttp.WriteBodyAsync(context.Response, request);
Assert(context.Response.StatusCode == StatusCodes.Status200OK
    && context.Response.ContentType == "application/json"
    && context.Response.Headers["X-TaxHub-Preview"] == "true"
    && context.Response.ContentLength == request.BodyBytes!.Value.Length,
    "Raw response metadata is incorrect.");
Assert(context.Response.Body is MemoryStream stream
    && stream.ToArray().AsSpan().SequenceEqual(request.BodyBytes!.Value.AsSpan()),
    "Raw response bytes were wrapped, reformatted or reserialized.");

var inspectionNames = typeof(PreparedApiRequestInspection).GetProperties().Select(property => property.Name).ToArray();
var forbidden = new[] { "ConnectionString", "Credential", "Password", "Token", "Authorization", "Exception" };
Assert(forbidden.All(term => inspectionNames.All(name => !name.Contains(term, StringComparison.OrdinalIgnoreCase))),
    "The safe inspection DTO exposes a credential, connection or exception member.");
var inspectionJson = JsonSerializer.Serialize(PreparedApiRequestInspection.From(new string('a', 32), request));
Assert(!inspectionJson.Contains("ConnectionString", StringComparison.OrdinalIgnoreCase)
    && !inspectionJson.Contains("Authorization", StringComparison.OrdinalIgnoreCase)
    && !inspectionJson.Contains("exact-bytes", StringComparison.Ordinal),
    "Inspection serialization leaked source connection, credential or raw-body content.");
Assert(typeof(BodylessRequestDescription).GetProperties().All(property => property.Name != "Body"),
    "A bodyless description exposes a placeholder body.");

var objectiveThreeControllers = new[]
{
    typeof(VatPreparationController),
    typeof(CumulativePreparationController),
    typeof(BodylessRequestDescriptionController)
};
var dependencies = objectiveThreeControllers.SelectMany(type => type.GetFields(
    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)).Select(field => field.FieldType).ToArray();
Assert(dependencies.All(type => !type.FullName!.Contains("Adapters.Submission", StringComparison.Ordinal)
    && !type.Name.Contains("HmrcSubmissionRunner", StringComparison.Ordinal)),
    "An Objective 3 controller can resolve the legacy submission adapter or runner.");

var secret = "Server=private;Password=secret";
var problem = PreparedRequestProblem.FromException(new Exception(secret), "correlation-1");
var problemJson = JsonSerializer.Serialize(problem);
Assert(problem.Status == StatusCodes.Status500InternalServerError
    && problem.Extensions["correlationId"]?.ToString() == "correlation-1"
    && !problemJson.Contains(secret, StringComparison.Ordinal),
    "Unexpected failures are not converted to correlation-safe problem details.");

var validatorBody = Encoding.UTF8.GetBytes("{\"specVersion\":\"3.3\",\"code\":\"VALID_HEADERS\"}");
var handler = new RecordingHandler(validatorBody);
using (var client = new HttpClient(handler))
using (var validator = new HmrcFraudHeaderValidator(client, EnvironmentSelector.Sandbox()))
using (var token = new ProtectedSecret("test-access-token"))
{
    var headers = new FraudPreventionHeaders(new Dictionary<string, string>
    {
        ["Gov-Client-Connection-Method"] = "WEB_APP_VIA_SERVER",
        ["Gov-Client-Screens"] = "width=1920&height=1080&scaling-factor=1.25&colour-depth=24"
    });
    var result = await validator.ValidateAsync(headers, token);
    Assert(handler.Method == HttpMethod.Get
        && handler.Uri == new Uri("https://test-api.service.hmrc.gov.uk/test/fraud-prevention-headers/validate")
        && handler.Accept == "application/vnd.hmrc.1.0+json",
        "The fraud validator did not use the pinned sandbox request contract.");
    Assert(handler.AuthorizationScheme == "Bearer" && handler.AuthorizationParameter == "test-access-token"
        && handler.FraudHeaders?["Gov-Client-Connection-Method"].Single() == "WEB_APP_VIA_SERVER",
        "The fraud validator did not attach the access token and formatted headers exactly once.");
    Assert(result.StatusCode == 200 && result.ContentType.StartsWith("application/json", StringComparison.Ordinal)
        && result.Body.AsSpan().SequenceEqual(validatorBody),
        "The bounded HMRC fraud-validator response was not preserved exactly.");
    Assert(!result.ToString().Contains("VALID_HEADERS", StringComparison.Ordinal),
        "A validator diagnostic representation exposed the response body.");
}

var diagnosticActions = typeof(HmrcSandboxDiagnosticsController)
    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var validateAction = diagnosticActions.Single(method => method.Name == nameof(HmrcSandboxDiagnosticsController.Validate));
var captureAction = diagnosticActions.Single(method => method.Name == nameof(HmrcSandboxDiagnosticsController.CaptureBrowserSession));
var signOutAction = diagnosticActions.Single(method => method.Name == nameof(HmrcSandboxDiagnosticsController.EndSession));
var signInAction = diagnosticActions.Single(method => method.Name == nameof(HmrcSandboxDiagnosticsController.BeginHostSignIn));
var disconnectAction = diagnosticActions.Single(method => method.Name == nameof(HmrcSandboxDiagnosticsController.Disconnect));
Assert(typeof(HmrcSandboxDiagnosticsController).GetCustomAttribute<AuthorizeAttribute>()?.AuthenticationSchemes
        == WebHarnessAuthenticationOptions.ApplicationScheme
    && signInAction.GetCustomAttribute<AllowAnonymousAttribute>() is not null,
    "The HMRC diagnostics are not protected by the Trade Control Identity session.");
Assert(validateAction.GetCustomAttribute<HttpGetAttribute>()?.Template == "fraud-prevention/validate"
    && validateAction.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
        [typeof(CancellationToken)])
    && diagnosticActions.Any(method => method.Name == nameof(HmrcSandboxDiagnosticsController.Callback)
        && method.GetCustomAttribute<HttpGetAttribute>()?.Template == "/VatMTD"),
    "The parameterless sandbox validator or registered OAuth callback endpoint is missing.");
Assert(validateAction.GetParameters().All(parameter => parameter.GetCustomAttribute<FromBodyAttribute>() is null)
    && captureAction.GetCustomAttribute<HttpPostAttribute>()?.Template == "fraud-prevention/browser-session"
    && captureAction.GetParameters().Single().GetCustomAttribute<FromBodyAttribute>() is not null,
    "Browser capture is not separate from the bodyless validator contract.");
Assert(signOutAction.GetCustomAttribute<HttpPostAttribute>()?.Template == "sign-out"
    && signOutAction.GetParameters().Length == 0,
    "The parameterless WebHarness sign-out endpoint is missing.");
Assert(disconnectAction.GetCustomAttribute<HttpPostAttribute>()?.Template == "disconnect"
    && disconnectAction.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(
        [typeof(CancellationToken)]),
    "The principal-bound HMRC disconnect endpoint is missing.");
var captureNames = typeof(BrowserFraudCapture).GetProperties().Select(property => property.Name).ToArray();
Assert(new[] { "Token", "Secret", "Password", "PublicIp", "License" }
        .All(term => captureNames.All(name => !name.Contains(term, StringComparison.OrdinalIgnoreCase))),
    "The browser capture DTO accepts a trusted or secret fraud-header fact.");
var swaggerActions = diagnosticActions.Where(method => method.Name is
    nameof(HmrcSandboxDiagnosticsController.Authorize)
    or nameof(HmrcSandboxDiagnosticsController.Callback)
    or nameof(HmrcSandboxDiagnosticsController.CaptureBrowserSession)
    or nameof(HmrcSandboxDiagnosticsController.Validate)
    or nameof(HmrcSandboxDiagnosticsController.EndSession)
    or nameof(HmrcSandboxDiagnosticsController.Disconnect));
Assert(swaggerActions.All(method => method.GetCustomAttribute<ApiExplorerSettingsAttribute>()?.IgnoreApi != true),
    "An HMRC sandbox diagnostic endpoint is hidden from Swagger.");

var reauthorisingDiagnostics = new ReauthorisingDiagnostics();
var diagnosticController = new HmrcSandboxDiagnosticsController(reauthorisingDiagnostics,
    Options.Create(new WebHarnessAuthenticationOptions()));
Assert(diagnosticController.BeginHostSignIn(null) is RedirectResult
    {
        Url: "https://localhost:44381/Identity/Account/Login?returnUrl=%2FTaxHub%2FHmrcDiagnosticsReturn"
    }
    && diagnosticController.BeginHostSignIn("validate") is RedirectResult
    {
        Url: "https://localhost:44381/Identity/Account/Login?returnUrl=%2FTaxHub%2FHmrcDiagnosticsReturn%3FreturnTo%3Dvalidate"
    }, "The WebHarness sign-in bridge does not use the fixed Trade Control Identity return paths.");
var diagnosticContext = new DefaultHttpContext();
diagnosticContext.Features.Set<ISessionFeature>(new TestSessionFeature { Session = new TestSession() });
var authentication = new TestAuthenticationService();
diagnosticContext.RequestServices = new ServiceCollection()
    .AddSingleton<IAuthenticationService>(authentication)
    .BuildServiceProvider();
diagnosticContext.User = new ClaimsPrincipal(new ClaimsIdentity(
    [new Claim(ClaimTypes.NameIdentifier, "identity-user-1"), new Claim(ClaimTypes.Name, "user@example.test")],
    WebHarnessAuthenticationOptions.ApplicationScheme));
diagnosticController.ControllerContext = new ControllerContext { HttpContext = diagnosticContext };
diagnosticContext.Connection.RemoteIpAddress = IPAddress.Loopback;
diagnosticContext.Connection.RemotePort = 54321;
diagnosticContext.Connection.LocalIpAddress = IPAddress.Loopback;
var deviceId = Guid.NewGuid();
var captureResult = diagnosticController.CaptureBrowserSession(new BrowserFraudCapture(
    "Mozilla/5.0 TaxHubTest", deviceId, [new(1920, 1080, 1.25m, 24)], "UTC+01:00", new(1256, 803)));
Assert(captureResult is NoContentResult, "Valid browser facts were not captured into the diagnostic session.");
var validationResult = await diagnosticController.Validate(CancellationToken.None);
Assert(validationResult is UnauthorizedObjectResult,
    "A missing grant was not returned as a Swagger-safe authorization requirement.");
Assert(diagnosticContext.Response.Headers["X-TaxHub-Hmrc-Authorize"] == "/diagnostics/hmrc/authorize",
    "A missing grant did not advertise the top-level authorization journey to Swagger.");
Assert(reauthorisingDiagnostics.LastActor == new HmrcSandboxActor("identity-user-1", "identity-user-1")
    && reauthorisingDiagnostics.LastFacts is { ClientPort: 54321 } facts
    && facts.Browser.JavascriptUserAgent == "Mozilla/5.0 TaxHubTest"
    && facts.Browser.DeviceId == deviceId
    && facts.Browser.Screens.Single() == new FraudScreen(1920, 1080, 1.25m, 24)
    && facts.Browser.Timezone == "UTC+01:00"
    && facts.Browser.WindowSize == new FraudWindowSize(1256, 803)
    && facts.Browser.UserIds["tax-hub-internal"] == "identity-user-1",
    "The validator did not bind browser facts and the OAuth grant to the authenticated Identity principal.");
var signOutResult = await diagnosticController.EndSession();
Assert(signOutResult is NoContentResult
    && authentication.SignedOutScheme == WebHarnessAuthenticationOptions.ApplicationScheme
    && reauthorisingDiagnostics.DisconnectedActor is null,
    "Sign-out did not end only the shared Trade Control Identity session.");
var disconnectResult = await diagnosticController.Disconnect(CancellationToken.None);
Assert(disconnectResult is NoContentResult
    && reauthorisingDiagnostics.DisconnectedActor == new HmrcSandboxActor("identity-user-1", "identity-user-1"),
    "HMRC disconnect did not retire the authenticated Identity principal's grant.");

var keyTestRoot = Path.Combine(Path.GetTempPath(), "TaxHub-WebHarnessTests", Guid.NewGuid().ToString("N"));
try
{
    var keyPath = Path.Combine(keyTestRoot, "oauth-store.key");
    var firstKey = HmrcSandboxDiagnostics.LoadOrCreateDevelopmentKey(keyPath);
    var restartedKey = HmrcSandboxDiagnostics.LoadOrCreateDevelopmentKey(keyPath);
    Assert(firstKey.Length == 32 && firstKey.AsSpan().SequenceEqual(restartedKey),
        "The development OAuth-store key did not survive a WebHarness restart.");
    Assert(new FileInfo(keyPath).Length == 32,
        "The persistent development OAuth-store key has an unexpected representation.");
    CryptographicOperations.ZeroMemory(firstKey);
    CryptographicOperations.ZeroMemory(restartedKey);
}
finally
{
    if (Directory.Exists(keyTestRoot)) Directory.Delete(keyTestRoot, recursive: true);
}

Console.WriteLine($"WebHarness hardening tests passed ({assertions} assertions).");

sealed class MutableTimeProvider(DateTimeOffset value) : TimeProvider
{
    private DateTimeOffset _value = value;
    public override DateTimeOffset GetUtcNow() => _value;
    public void Advance(TimeSpan duration) => _value += duration;
}

sealed class RecordingHandler(byte[] responseBody) : HttpMessageHandler
{
    public HttpMethod? Method { get; private set; }
    public Uri? Uri { get; private set; }
    public string? Accept { get; private set; }
    public string? AuthorizationScheme { get; private set; }
    public string? AuthorizationParameter { get; private set; }
    public IReadOnlyDictionary<string, IEnumerable<string>>? FraudHeaders { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        Method = request.Method;
        Uri = request.RequestUri;
        Accept = request.Headers.Accept.Single().MediaType;
        AuthorizationScheme = request.Headers.Authorization?.Scheme;
        AuthorizationParameter = request.Headers.Authorization?.Parameter;
        FraudHeaders = request.Headers.Where(header => header.Key.StartsWith("Gov-", StringComparison.Ordinal))
            .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(responseBody)
            {
                Headers = { ContentType = new("application/json") }
            }
        });
    }
}

sealed class ReauthorisingDiagnostics : IHmrcSandboxDiagnostics
{
    public bool Enabled => true;
    public HmrcSandboxActor? LastActor { get; private set; }
    public HmrcSandboxActor? DisconnectedActor { get; private set; }
    public CollectedFraudSessionFacts? LastFacts { get; private set; }
    public Task<OAuthAuthorisationStart> BeginAuthorisationAsync(HmrcSandboxActor actor,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new OAuthAuthorisationStart(
            new Uri("https://test-www.tax.service.gov.uk/oauth/authorize?state=redacted"),
            DateTimeOffset.UtcNow.AddMinutes(10)));
    public Task<OAuthAccessOutcome> CompleteCallbackAsync(HmrcSandboxActor actor, OAuthCallback callback,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DisconnectAsync(HmrcSandboxActor actor, CancellationToken cancellationToken = default)
    {
        DisconnectedActor = actor;
        return Task.CompletedTask;
    }
    public Task<FraudHeaderDiagnosticOutcome> ValidateAsync(HmrcSandboxActor actor,
        CollectedFraudSessionFacts facts,
        CancellationToken cancellationToken = default)
    {
        LastActor = actor;
        LastFacts = facts;
        return Task.FromResult(new FraudHeaderDiagnosticOutcome(null, OAuthReauthorisationReason.MissingGrant));
    }
}

sealed class TestAuthenticationService : IAuthenticationService
{
    public string? SignedOutScheme { get; private set; }
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
        Task.FromResult(AuthenticateResult.NoResult());
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
        Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
        Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal,
        AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties)
    {
        SignedOutScheme = scheme;
        return Task.CompletedTask;
    }
}

sealed class TestSessionFeature : ISessionFeature
{
    public required ISession Session { get; set; }
}

sealed class TestSession : ISession
{
    private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);
    public bool IsAvailable => true;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public IEnumerable<string> Keys => _values.Keys;
    public void Clear() => _values.Clear();
    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Remove(string key) => _values.Remove(key);
    public void Set(string key, byte[] value) => _values[key] = value.ToArray();
    public bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value)
    {
        if (_values.TryGetValue(key, out var stored))
        {
            value = stored.ToArray();
            return true;
        }
        value = null;
        return false;
    }
}
