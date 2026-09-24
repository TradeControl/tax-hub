using System.Security.Cryptography;
using System.Text;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.OAuth;

public sealed class HmrcOAuthService : IDisposable
{
    private readonly EnvironmentSelector _environment;
    private readonly HmrcOAuthOptions _options;
    private readonly ISecretProvider _secrets;
    private readonly IOAuthGrantStore _store;
    private readonly IHmrcOAuthTokenEndpoint _tokens;
    private readonly TimeProvider _clock;

    internal HmrcOAuthService(EnvironmentSelector environment, HmrcOAuthOptions options,
        ISecretProvider secrets, IOAuthGrantStore store, IHmrcOAuthTokenEndpoint tokens,
        TimeProvider? clock = null)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _clock = clock ?? TimeProvider.System;
        if (_environment.Selected.Environment != AuthorityEnvironment.Sandbox)
            throw new InvalidOperationException("OAuth production activation has not been reviewed.");
    }

    public static HmrcOAuthService CreateFileBackedSandbox(string grantStorePath,
        ReadOnlySpan<byte> encryptionKey, ISecretProvider secrets, IHmrcOAuthTokenEndpoint tokens,
        TimeProvider? clock = null)
    {
        var cipher = new AesGcmOAuthStoreCipher(encryptionKey);
        var service = new HmrcOAuthService(EnvironmentSelector.Sandbox(), HmrcOAuthOptions.LocalSandbox,
            secrets, new FileOAuthGrantStore(grantStorePath, cipher), tokens, clock);
        service._ownedResourceHolder = cipher;
        return service;
    }

    private IDisposable? _ownedResourceHolder;

    public async Task<OAuthAuthorisationStart> BeginAuthorisationAsync(AuthorityDispatchContext context,
        string requiredScope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireEnabledScope(requiredScope);
        using var clientId = await _secrets.ResolveAsync(HmrcSecretReferences.ClientId, cancellationToken);
        var clientIdText = Copy(clientId);
        try
        {
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));
            var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
            var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var expires = _clock.GetUtcNow() + _options.PendingAuthorisationLifetime;
            await _store.SavePendingAsync(new(state, verifier, context.TenantReference,
                context.AuthorisationPrincipalReference, context.ActorReference, requiredScope,
                _options.RedirectUri.AbsoluteUri, expires), cancellationToken);
            var uri = BuildAuthorisationUri(clientIdText, requiredScope, state, challenge);
            return new(uri, expires);
        }
        finally { Clear(ref clientIdText); }
    }

    public async Task<OAuthAccessOutcome> CompleteCallbackAsync(AuthorityDispatchContext context,
        OAuthCallback callback, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(callback);
        if (string.IsNullOrWhiteSpace(callback.State))
            throw new OAuthCallbackValidationException("The OAuth callback state is missing.");
        var pending = await _store.ConsumePendingAsync(callback.State, context.TenantReference,
            context.AuthorisationPrincipalReference, context.ActorReference, _clock.GetUtcNow(), cancellationToken)
            ?? throw new OAuthCallbackValidationException("The OAuth callback state is invalid or expired.");
        if (callback.Error is not null)
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.AuthorisationDeclined);
        if (string.IsNullOrWhiteSpace(callback.Code))
            throw new OAuthCallbackValidationException("The OAuth callback code is missing.");
        if (pending.RedirectUri != _options.RedirectUri.AbsoluteUri)
            throw new OAuthCallbackValidationException("The OAuth callback redirect binding is invalid.");

        var response = await ExchangeAsync(callback.Code, pending.CodeVerifier, cancellationToken);
        var grantedScopes = response.Scopes.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal) { pending.RequiredScope }
            : new HashSet<string>(response.Scopes, StringComparer.Ordinal);
        if (!grantedScopes.Contains(pending.RequiredScope))
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.ScopeNotGranted);
        var now = _clock.GetUtcNow();
        if (response.AccessTokenLifetime <= _options.AccessTokenExpirySkew)
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.ExpiredGrant);
        await _store.SaveGrantAsync(new(context.TenantReference, context.AuthorisationPrincipalReference,
            pending.RequiredScope, response.AccessToken, response.RefreshToken,
            grantedScopes,
            now + response.AccessTokenLifetime, now.AddMonths(_options.GrantLifetimeMonths)), cancellationToken);
        return OAuthAccessOutcome.Available(response.AccessToken, now + response.AccessTokenLifetime);
    }

    public async Task<OAuthAccessOutcome> GetAccessAsync(AuthorityDispatchContext context, string requiredScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireEnabledScope(requiredScope);
        await using var lease = await _store.AcquireRefreshLeaseAsync(context.TenantReference,
            context.AuthorisationPrincipalReference, requiredScope, cancellationToken);
        var grant = await _store.GetGrantAsync(context.TenantReference, context.AuthorisationPrincipalReference,
            requiredScope, cancellationToken);
        if (grant is null) return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.MissingGrant);
        if (grant.Revoked) return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.RevokedGrant);
        if (!grant.Scopes.Contains(requiredScope))
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.ScopeNotGranted);
        var now = _clock.GetUtcNow();
        if (grant.GrantExpiresAtUtc <= now)
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.ExpiredGrant);
        if (grant.AccessTokenExpiresAtUtc > now + _options.AccessTokenExpirySkew)
            return OAuthAccessOutcome.Available(grant.AccessToken, grant.AccessTokenExpiresAtUtc);

        OAuthTokenResponse refreshed;
        try { refreshed = await RefreshAsync(grant.RefreshToken, cancellationToken); }
        catch (OAuthTokenEndpointException exception) when (exception.RequiresReauthorisation)
        {
            await _store.RevokeAsync(context.TenantReference, context.AuthorisationPrincipalReference,
                requiredScope, cancellationToken);
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.RefreshRejected);
        }
        if (FixedEquals(grant.RefreshToken, refreshed.RefreshToken))
        {
            await _store.RevokeAsync(context.TenantReference, context.AuthorisationPrincipalReference,
                requiredScope, cancellationToken);
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.RefreshRejected);
        }
        if (refreshed.AccessTokenLifetime <= _options.AccessTokenExpirySkew)
        {
            await _store.RevokeAsync(context.TenantReference, context.AuthorisationPrincipalReference,
                requiredScope, cancellationToken);
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.RefreshRejected);
        }
        var scopes = refreshed.Scopes.Count == 0
            ? new HashSet<string>(grant.Scopes, StringComparer.Ordinal)
            : new HashSet<string>(refreshed.Scopes, StringComparer.Ordinal);
        if (!scopes.Contains(requiredScope))
        {
            await _store.RevokeAsync(context.TenantReference, context.AuthorisationPrincipalReference,
                requiredScope, cancellationToken);
            return OAuthAccessOutcome.Reauthorise(OAuthReauthorisationReason.ScopeNotGranted);
        }
        var updated = grant with
        {
            AccessToken = refreshed.AccessToken,
            RefreshToken = refreshed.RefreshToken,
            Scopes = scopes,
            AccessTokenExpiresAtUtc = now + refreshed.AccessTokenLifetime
        };
        await _store.SaveGrantAsync(updated, cancellationToken);
        return OAuthAccessOutcome.Available(updated.AccessToken, updated.AccessTokenExpiresAtUtc);
    }

    public Task RevokeAsync(AuthorityDispatchContext context, string requiredScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        RequireEnabledScope(requiredScope);
        return _store.RevokeAsync(context.TenantReference, context.AuthorisationPrincipalReference,
            requiredScope, cancellationToken);
    }

    private async Task<OAuthTokenResponse> ExchangeAsync(string code, string verifier,
        CancellationToken cancellationToken)
    {
        using var clientId = await _secrets.ResolveAsync(HmrcSecretReferences.ClientId, cancellationToken);
        using var clientSecret = await _secrets.ResolveAsync(HmrcSecretReferences.ClientSecret, cancellationToken);
        var id = Copy(clientId); var secret = Copy(clientSecret);
        try { return await _tokens.ExchangeCodeAsync(id, secret, code, verifier, _options.RedirectUri, cancellationToken); }
        finally { Clear(ref id); Clear(ref secret); }
    }

    private async Task<OAuthTokenResponse> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        using var clientId = await _secrets.ResolveAsync(HmrcSecretReferences.ClientId, cancellationToken);
        using var clientSecret = await _secrets.ResolveAsync(HmrcSecretReferences.ClientSecret, cancellationToken);
        var id = Copy(clientId); var secret = Copy(clientSecret);
        try { return await _tokens.RefreshAsync(id, secret, refreshToken, cancellationToken); }
        finally { Clear(ref id); Clear(ref secret); }
    }

    private Uri BuildAuthorisationUri(string clientId, string scope, string state, string challenge)
    {
        var endpoint = new Uri(_environment.Selected.AuthorisationBaseUri, "/oauth/authorize");
        var values = new Dictionary<string, string>
        {
            ["response_type"] = "code", ["client_id"] = clientId, ["scope"] = scope,
            ["redirect_uri"] = _options.RedirectUri.AbsoluteUri, ["state"] = state,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256"
        };
        return new UriBuilder(endpoint) { Query = string.Join("&", values.Select(item =>
            $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}")) }.Uri;
    }

    private static void RequireEnabledScope(string scope)
    {
        if (!HmrcOAuthScopes.IsEnabled(scope))
            throw new InvalidOperationException("The requested HMRC OAuth scope is not enabled in this phase.");
    }

    private static string Copy(ProtectedSecret secret)
    {
        var buffer = new char[secret.Length];
        try { secret.CopyTo(buffer); return new string(buffer); }
        finally { Array.Clear(buffer); }
    }

    private static void Clear(ref string value) => value = string.Empty;
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left); var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public void Dispose()
    {
        _ownedResourceHolder?.Dispose();
        _ownedResourceHolder = null;
    }
}
