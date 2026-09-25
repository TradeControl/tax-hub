using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Hmrc;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = WebHarnessAuthenticationOptions.ApplicationScheme)]
[Route("diagnostics/hmrc")]
public sealed class HmrcSandboxDiagnosticsController(
    IHmrcSandboxDiagnostics diagnostics,
    IOptions<WebHarnessAuthenticationOptions> authenticationOptions) : ControllerBase
{
    private const string BrowserSessionKey = "tax-hub-fraud-browser-v1";

    [AllowAnonymous]
    [HttpGet("sign-in")]
    public IActionResult BeginHostSignIn([FromQuery] string? returnTo)
    {
        if (!diagnostics.Enabled) return NotFound();
        return Redirect(authenticationOptions.Value.LoginUri(
            string.Equals(returnTo, "validate", StringComparison.Ordinal)).AbsoluteUri);
    }

    [HttpGet("authorize")]
    public async Task<IActionResult> Authorize(CancellationToken cancellationToken)
    {
        if (!diagnostics.Enabled) return NotFound();
        var start = await diagnostics.BeginAuthorisationAsync(Actor(), cancellationToken);
        return Redirect(start.AuthorisationUri.AbsoluteUri);
    }

    [HttpGet("/VatMTD")]
    public async Task<IActionResult> Callback([FromQuery] string? state, [FromQuery] string? code,
        [FromQuery] string? error, CancellationToken cancellationToken)
    {
        if (!diagnostics.Enabled) return NotFound();
        using var outcome = await diagnostics.CompleteCallbackAsync(Actor(),
            new OAuthCallback(state ?? string.Empty, code, error), cancellationToken);
        if (outcome.Kind != OAuthAccessOutcomeKind.Available)
            return Unauthorized(new
            {
                status = "reauthorization-required",
                reason = outcome.ReauthorisationReason?.ToString()
            });

        return Redirect("/diagnostics/hmrc/fraud-prevention/validate");
    }

    [HttpPost("sign-out")]
    public async Task<IActionResult> EndSession()
    {
        if (!diagnostics.Enabled) return NotFound();

        await HttpContext.SignOutAsync(WebHarnessAuthenticationOptions.ApplicationScheme);
        return NoContent();
    }

    [HttpPost("disconnect")]
    public async Task<IActionResult> Disconnect(CancellationToken cancellationToken)
    {
        if (!diagnostics.Enabled) return NotFound();

        await diagnostics.DisconnectAsync(Actor(), cancellationToken);
        return NoContent();
    }

    [HttpPost("fraud-prevention/browser-session")]
    public IActionResult CaptureBrowserSession([FromBody] BrowserFraudCapture capture)
    {
        if (!diagnostics.Enabled) return NotFound();

        // Constructing the value validates all browser-controlled fields before they enter the session.
        _ = ToBrowserFacts(capture, new ClaimsPrincipal());
        HttpContext.Session.SetString(BrowserSessionKey, JsonSerializer.Serialize(capture));
        return NoContent();
    }

    [HttpGet("fraud-prevention/validate")]
    public async Task<IActionResult> Validate(CancellationToken cancellationToken)
    {
        if (!diagnostics.Enabled) return NotFound();
        var capturedJson = HttpContext.Session.GetString(BrowserSessionKey);
        if (string.IsNullOrWhiteSpace(capturedJson))
            return Conflict(new
            {
                status = "browser-facts-required",
                detail = "Reload Swagger so the WebHarness can capture browser and session facts before validation."
            });

        BrowserFraudCapture? capture;
        try { capture = JsonSerializer.Deserialize<BrowserFraudCapture>(capturedJson); }
        catch (JsonException) { capture = null; }
        if (capture is null)
        {
            HttpContext.Session.Remove(BrowserSessionKey);
            return Conflict(new
            {
                status = "browser-facts-required",
                detail = "The captured browser facts are invalid. Reload Swagger and try again."
            });
        }

        var connection = HttpContext.Connection;
        var clientAddress = connection.RemoteIpAddress
            ?? throw new InvalidOperationException("The client socket address is unavailable.");
        var serverAddress = connection.LocalIpAddress
            ?? throw new InvalidOperationException("The server socket address is unavailable.");
        var clientPort = connection.RemotePort;
        var configuration = HttpContext.RequestServices.GetService<IConfiguration>();
        if (configuration is not null
            && IPAddress.TryParse(configuration["TaxHub:HmrcSandbox:PublicServerAddress"], out var publicServer))
        {
            serverAddress = publicServer;
            if (TryParseForwardedClient(Request.Headers["X-Forwarded-For"].ToString(), out var forwardedAddress,
                    out var forwardedPort))
            {
                clientAddress = forwardedAddress;
                clientPort = forwardedPort ?? clientPort;
            }
        }
        var browser = ToBrowserFacts(capture, User);
        var facts = new CollectedFraudSessionFacts(browser, Normalize(clientAddress), clientPort,
            Normalize(serverAddress));
        var outcome = await diagnostics.ValidateAsync(Actor(), facts, cancellationToken);
        if (outcome.RequiresAuthorisation)
        {
            Response.Headers["X-TaxHub-Hmrc-Authorize"] = "/diagnostics/hmrc/authorize";
            return Unauthorized(new
            {
                status = "reauthorization-required",
                reason = outcome.ReauthorisationReason?.ToString(),
                authorize = "/diagnostics/hmrc/authorize"
            });
        }

        var response = outcome.Response!;
        Response.StatusCode = response.StatusCode;
        Response.ContentType = response.ContentType;
        Response.ContentLength = response.Body.Length;
        await Response.Body.WriteAsync(response.Body, cancellationToken);
        return new EmptyResult();
    }

    private HmrcSandboxActor Actor()
    {
        var principal = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (User.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(principal))
            throw new InvalidOperationException("An authenticated Trade Control principal is required.");
        return new(principal, principal);
    }

    private static BrowserFraudFacts ToBrowserFacts(BrowserFraudCapture capture, ClaimsPrincipal user) =>
        new(capture.JavascriptUserAgent, capture.DeviceId, CollectMultiFactor(user), capture.Screens,
            capture.Timezone, CollectUserIds(user), capture.WindowSize);

    private static IReadOnlyDictionary<string, string> CollectUserIds(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return new Dictionary<string, string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var subject = user.FindFirstValue(ClaimTypes.NameIdentifier);
        var name = user.Identity.Name;
        if (!string.IsNullOrWhiteSpace(subject)) values["tax-hub-internal"] = subject;
        if (!string.IsNullOrWhiteSpace(name)) values["tax-hub"] = name;
        return values;
    }

    private static IReadOnlyList<FraudMultiFactorEvent> CollectMultiFactor(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return [];
        var typeText = user.FindFirstValue("tax-hub:mfa-type");
        var timestampText = user.FindFirstValue("tax-hub:mfa-timestamp");
        var reference = user.FindFirstValue("tax-hub:mfa-reference");
        if (!Enum.TryParse<FraudMultiFactorType>(typeText, ignoreCase: true, out var type)
            || !DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
            || string.IsNullOrWhiteSpace(reference)) return [];
        return [new(type, timestamp, reference)];
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static bool TryParseForwardedClient(string value, out IPAddress address, out int? port)
    {
        address = IPAddress.None;
        port = null;
        var first = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(first)) return false;
        if (IPEndPoint.TryParse(first, out var endpoint))
        {
            address = Normalize(endpoint.Address);
            port = endpoint.Port;
            return true;
        }
        return IPAddress.TryParse(first.Trim('[', ']'), out address!);
    }
}

public sealed class WebHarnessAuthenticationOptions
{
    public const string ApplicationScheme = "Identity.Application";
    public const string SectionName = "TaxHub:HostAuthentication";

    public Uri TradeControlOrigin { get; set; } = new("https://localhost:44381");

    public Uri LoginUri(bool resumeValidation)
    {
        var returnPath = resumeValidation
            ? "/TaxHub/HmrcDiagnosticsReturn?returnTo=validate"
            : "/TaxHub/HmrcDiagnosticsReturn";
        return new Uri(TradeControlOrigin,
            $"/Identity/Account/Login?returnUrl={Uri.EscapeDataString(returnPath)}");
    }
}
