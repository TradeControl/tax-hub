using TradeControl.Tax.UK.Adapters.Submission.Configuration;

namespace TradeControl.Tax.UK.Adapters.Submission.OAuth;

public static class HmrcOAuthScopes
{
    public const string ReadVat = "read:vat";
    public const string WriteVat = "write:vat";

    // Represented for prepared artifacts, but deliberately closed until Phase 5.15.
    public const string ReadSelfAssessment = "read:self-assessment";
    public const string WriteSelfAssessment = "write:self-assessment";

    public static bool IsEnabled(string scope) => scope is ReadVat or WriteVat;
}

public sealed record HmrcOAuthOptions(
    Uri RedirectUri,
    TimeSpan PendingAuthorisationLifetime,
    TimeSpan AccessTokenExpirySkew,
    int GrantLifetimeMonths)
{
    public static HmrcOAuthOptions LocalSandbox { get; } = new(
        new Uri("https://localhost:44362/VatMTD", UriKind.Absolute),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(5),
        18);

    public void Validate()
    {
        if (RedirectUri != LocalSandbox.RedirectUri)
            throw new InvalidOperationException("The OAuth callback is not the approved sandbox host entry point.");
        if (PendingAuthorisationLifetime <= TimeSpan.Zero || PendingAuthorisationLifetime > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("The pending authorisation lifetime is invalid.");
        if (AccessTokenExpirySkew < TimeSpan.Zero || AccessTokenExpirySkew > TimeSpan.FromMinutes(15))
            throw new InvalidOperationException("The access-token expiry skew is invalid.");
        if (GrantLifetimeMonths is < 1 or > 18)
            throw new InvalidOperationException("The OAuth grant lifetime is invalid.");
    }
}

public sealed record OAuthAuthorisationStart(Uri AuthorisationUri, DateTimeOffset ExpiresAtUtc)
{
    public override string ToString() => $"OAuthAuthorisationStart {{ AuthorisationUri = [REDACTED], ExpiresAtUtc = {ExpiresAtUtc:O} }}";
}

public sealed record OAuthCallback(string State, string? Code = null, string? Error = null)
{
    public override string ToString() => "OAuthCallback { State = [REDACTED], Code = [REDACTED], Error = [REDACTED] }";
}

public enum OAuthReauthorisationReason
{
    MissingGrant,
    ExpiredGrant,
    RevokedGrant,
    ScopeNotGranted,
    AuthorisationDeclined,
    RefreshRejected
}

public enum OAuthAccessOutcomeKind { Available, ReauthorisationRequired }

public sealed class OAuthAccessOutcome : IDisposable
{
    private OAuthAccessOutcome(OAuthAccessOutcomeKind kind, ProtectedSecret? accessToken,
        DateTimeOffset? expiresAtUtc, OAuthReauthorisationReason? reason)
    {
        Kind = kind;
        AccessToken = accessToken;
        ExpiresAtUtc = expiresAtUtc;
        ReauthorisationReason = reason;
    }

    public OAuthAccessOutcomeKind Kind { get; }
    public ProtectedSecret? AccessToken { get; }
    public DateTimeOffset? ExpiresAtUtc { get; }
    public OAuthReauthorisationReason? ReauthorisationReason { get; }

    internal static OAuthAccessOutcome Available(string token, DateTimeOffset expiresAtUtc) =>
        new(OAuthAccessOutcomeKind.Available, new ProtectedSecret(token), expiresAtUtc, null);

    internal static OAuthAccessOutcome Reauthorise(OAuthReauthorisationReason reason) =>
        new(OAuthAccessOutcomeKind.ReauthorisationRequired, null, null, reason);

    public void Dispose() => AccessToken?.Dispose();
}

public sealed class OAuthCallbackValidationException : InvalidOperationException
{
    public OAuthCallbackValidationException(string message) : base(message) { }
}

public sealed class OAuthTokenEndpointException : InvalidOperationException
{
    public OAuthTokenEndpointException(string safeErrorCode, bool requiresReauthorisation = false)
        : base("The HMRC OAuth token endpoint rejected the request.")
    {
        SafeErrorCode = string.IsNullOrWhiteSpace(safeErrorCode)
            || safeErrorCode.Length > 64
            || safeErrorCode.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-')
            ? "TOKEN-ENDPOINT-ERROR" : safeErrorCode;
        RequiresReauthorisation = requiresReauthorisation;
    }

    public string SafeErrorCode { get; }
    public bool RequiresReauthorisation { get; }
}

public sealed record OAuthTokenResponse(
    string AccessToken,
    string RefreshToken,
    TimeSpan AccessTokenLifetime,
    IReadOnlySet<string> Scopes)
{
    public override string ToString() => "OAuthTokenResponse { AccessToken = [REDACTED], RefreshToken = [REDACTED] }";
}

public interface IHmrcOAuthTokenEndpoint
{
    Task<OAuthTokenResponse> ExchangeCodeAsync(string clientId, string clientSecret, string code,
        string codeVerifier, Uri redirectUri, CancellationToken cancellationToken = default);

    Task<OAuthTokenResponse> RefreshAsync(string clientId, string clientSecret, string refreshToken,
        CancellationToken cancellationToken = default);
}
