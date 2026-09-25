using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class HostAuthenticationController(IConfiguration configuration, IMemoryCache memoryCache) : ControllerBase
{
    private sealed record Ticket(string Subject, string Name, string ReturnTo, long ExpiresUtc, string Nonce);

    [HttpGet("/diagnostics/hmrc/host-sign-in")]
    public async Task<IActionResult> SignIn([FromQuery] string ticket)
    {
        var secretText = configuration["TaxHub:HostAuthentication:HandoffSecret"];
        if (string.IsNullOrWhiteSpace(secretText)) return NotFound();
        var parts = (ticket ?? string.Empty).Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return Unauthorized();

        byte[] secret;
        byte[] signature;
        byte[] supplied;
        try
        {
            secret = Convert.FromBase64String(secretText);
            signature = HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(parts[0]));
            supplied = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(parts[1]);
        }
        catch (FormatException) { return Unauthorized(); }
        finally { }

        try
        {
            if (secret.Length < 32 || !CryptographicOperations.FixedTimeEquals(signature, supplied))
                return Unauthorized();
            var payload = JsonSerializer.Deserialize<Ticket>(
                Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(parts[0]));
            if (payload is null || string.IsNullOrWhiteSpace(payload.Subject)
                || string.IsNullOrWhiteSpace(payload.Nonce)
                || payload.ExpiresUtc < DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || payload.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(3).ToUnixTimeSeconds())
                return Unauthorized();
            var replayKey = $"host-authentication-ticket:{payload.Nonce}";
            if (memoryCache.TryGetValue(replayKey, out _)) return Unauthorized();
            memoryCache.Set(replayKey, true, TimeSpan.FromMinutes(3));
            var returnTo = payload.ReturnTo == "validate"
                ? "/diagnostics/hmrc/fraud-prevention/validate"
                : "/swagger/index.html";
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, payload.Subject),
                new Claim(ClaimTypes.Name, payload.Name)
            ], WebHarnessAuthenticationOptions.ApplicationScheme);
            await HttpContext.SignInAsync(WebHarnessAuthenticationOptions.ApplicationScheme,
                new ClaimsPrincipal(identity));
            return Redirect(returnTo);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(supplied);
        }
    }
}
