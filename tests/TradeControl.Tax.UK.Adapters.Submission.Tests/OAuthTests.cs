using System.Collections.Concurrent;
using System.Web;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;
using TradeControl.Tax.UK.Application.Preparation;

internal static class OAuthTests
{
    public static async Task<int> RunAsync(string root, ISecretProvider secrets)
    {
        var assertions = 0;
        void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new InvalidOperationException(message);
        }

        async Task AssertCallbackRejectedAsync(Func<Task> action, string message)
        {
            assertions++;
            try { await action(); }
            catch (OAuthCallbackValidationException) { return; }
            throw new InvalidOperationException(message);
        }

        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero));
        var endpoint = new FakeTokenEndpoint();
        var storePath = Path.Combine(root, "oauth", "grants.enc");
        var key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        using var service = HmrcOAuthService.CreateFileBackedSandbox(storePath, key, secrets, endpoint, clock);
        var context = Context("tenant-a", "principal-a", "actor-a");

        const string firstCode = "synthetic-code-never-persist";
        const string firstAccess = "synthetic-access-token-one";
        const string firstRefresh = "synthetic-refresh-token-one";
        endpoint.ExchangeResponse = Token(firstAccess, firstRefresh, HmrcOAuthScopes.ReadVat, TimeSpan.FromMinutes(6));
        var start = await service.BeginAuthorisationAsync(context, HmrcOAuthScopes.ReadVat);
        var query = HttpUtility.ParseQueryString(start.AuthorisationUri.Query);
        var state = query["state"]!;
        Assert(start.AuthorisationUri.Host == "test-www.tax.service.gov.uk"
            && start.AuthorisationUri.AbsolutePath == "/oauth/authorize"
            && query["redirect_uri"] == HmrcOAuthOptions.LocalSandbox.RedirectUri.AbsoluteUri
            && query["scope"] == HmrcOAuthScopes.ReadVat
            && query["code_challenge_method"] == "S256"
            && !string.IsNullOrWhiteSpace(query["code_challenge"])
            && query["client_secret"] is null,
            "The authorization request did not use the fixed callback, exact scope and S256 PKCE boundary.");
        Assert(!start.ToString().Contains(state, StringComparison.Ordinal)
            && !new OAuthCallback(state, firstCode).ToString().Contains(firstCode, StringComparison.Ordinal)
            && !endpoint.ExchangeResponse.ToString().Contains(firstAccess, StringComparison.Ordinal),
            "An OAuth diagnostic representation exposed state, code or token material.");
        await AssertCallbackRejectedAsync(() => service.CompleteCallbackAsync(context,
            new("wrong-state", firstCode)), "A mismatched callback state was accepted.");
        await AssertCallbackRejectedAsync(() => service.CompleteCallbackAsync(
            Context("tenant-a", "principal-a", "actor-b"), new(state, firstCode)),
            "A different actor completed the initiating actor's callback.");
        using (var completed = await service.CompleteCallbackAsync(context, new(state, firstCode)))
            Assert(completed.Kind == OAuthAccessOutcomeKind.Available,
                "A valid state-bound authorization callback did not create a grant.");
        await AssertCallbackRejectedAsync(() => service.CompleteCallbackAsync(context,
            new(state, firstCode)), "A consumed callback state was replayed.");
        Assert(endpoint.ExchangeCalls == 1 && endpoint.LastRedirectUri == HmrcOAuthOptions.LocalSandbox.RedirectUri
            && !string.IsNullOrWhiteSpace(endpoint.LastCodeVerifier),
            "Code exchange did not retain the exact redirect and PKCE verifier binding.");

        using (var otherTenant = await service.GetAccessAsync(Context("tenant-b", "principal-a", "actor-b"),
            HmrcOAuthScopes.ReadVat))
            Assert(otherTenant.ReauthorisationReason == OAuthReauthorisationReason.MissingGrant,
                "An OAuth grant crossed a tenant boundary.");

        endpoint.RefreshResponse = Token("synthetic-access-token-two", "synthetic-refresh-token-two",
            HmrcOAuthScopes.ReadVat, TimeSpan.FromHours(4));
        endpoint.RefreshDelay = TimeSpan.FromMilliseconds(80);
        clock.Advance(TimeSpan.FromMinutes(2));
        var concurrent = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ =>
            service.GetAccessAsync(context, HmrcOAuthScopes.ReadVat)));
        try
        {
            Assert(endpoint.RefreshCalls == 1 && concurrent.All(result => result.Kind == OAuthAccessOutcomeKind.Available),
                "Concurrent access performed more than one single-use refresh.");
        }
        finally { foreach (var result in concurrent) result.Dispose(); }
        Assert(endpoint.RefreshTokens.Single() == firstRefresh,
            "The first single-use refresh token was not rotated exactly once.");

        endpoint.RefreshDelay = TimeSpan.Zero;
        endpoint.RefreshResponse = Token("synthetic-access-token-three", "synthetic-refresh-token-three",
            HmrcOAuthScopes.ReadVat, TimeSpan.FromHours(4));
        clock.Advance(TimeSpan.FromHours(4));
        using (var refreshedAgain = await service.GetAccessAsync(context, HmrcOAuthScopes.ReadVat))
            Assert(refreshedAgain.Kind == OAuthAccessOutcomeKind.Available
                && endpoint.RefreshTokens.Last() == "synthetic-refresh-token-two",
                "Refresh rotation did not persist the replacement token atomically.");

        await service.RevokeAsync(context, HmrcOAuthScopes.ReadVat);
        using (var revoked = await service.GetAccessAsync(context, HmrcOAuthScopes.ReadVat))
            Assert(revoked.ReauthorisationReason == OAuthReauthorisationReason.RevokedGrant,
                "A locally revoked principal grant remained usable.");

        endpoint.ExchangeResponse = Token("rejected-refresh-access", "rejected-refresh-token",
            HmrcOAuthScopes.ReadVat, TimeSpan.FromMinutes(6));
        var rejectedRefreshStart = await service.BeginAuthorisationAsync(context, HmrcOAuthScopes.ReadVat);
        using (var _ = await service.CompleteCallbackAsync(context,
            new(State(rejectedRefreshStart), "rejected-refresh-code"))) { }
        endpoint.RefreshException = new OAuthTokenEndpointException("invalid_grant", true);
        clock.Advance(TimeSpan.FromMinutes(2));
        using (var rejectedRefresh = await service.GetAccessAsync(context, HmrcOAuthScopes.ReadVat))
            Assert(rejectedRefresh.ReauthorisationReason == OAuthReauthorisationReason.RefreshRejected,
                "A rejected single-use refresh did not retire the grant and require reauthorization.");
        endpoint.RefreshException = null;

        endpoint.ExchangeResponse = Token("wrong-scope-access", "wrong-scope-refresh",
            HmrcOAuthScopes.ReadVat, TimeSpan.FromHours(4));
        var wrongScopeStart = await service.BeginAuthorisationAsync(context, HmrcOAuthScopes.WriteVat);
        using (var wrongScope = await service.CompleteCallbackAsync(context,
            new(State(wrongScopeStart), "wrong-scope-code")))
            Assert(wrongScope.ReauthorisationReason == OAuthReauthorisationReason.ScopeNotGranted,
                "A token without the required prepared-request scope was stored.");

        endpoint.ExchangeResponse = Token("expired-access", "expired-refresh",
            HmrcOAuthScopes.WriteVat, TimeSpan.FromHours(4));
        var expiringStart = await service.BeginAuthorisationAsync(context, HmrcOAuthScopes.WriteVat);
        using (var _ = await service.CompleteCallbackAsync(context, new(State(expiringStart), "expiry-code"))) { }
        clock.Advance(TimeSpan.FromDays(549));
        using (var expired = await service.GetAccessAsync(context, HmrcOAuthScopes.WriteVat))
            Assert(expired.ReauthorisationReason == OAuthReauthorisationReason.ExpiredGrant,
                "An expired 18-month OAuth grant remained usable.");

        var deniedStart = await service.BeginAuthorisationAsync(context, HmrcOAuthScopes.ReadVat);
        using (var denied = await service.CompleteCallbackAsync(context,
            new(State(deniedStart), Error: "access_denied")))
            Assert(denied.ReauthorisationReason == OAuthReauthorisationReason.AuthorisationDeclined,
                "An authorization denial did not return the typed reauthorization outcome.");

        var encrypted = await File.ReadAllTextAsync(storePath);
        Assert(new[] { firstCode, firstAccess, firstRefresh, state, endpoint.LastCodeVerifier!,
                "synthetic-access-token-two", "synthetic-refresh-token-two", "wrong-scope-code", "expiry-code",
                "rejected-refresh-code", "rejected-refresh-access", "rejected-refresh-token" }
            .All(value => !encrypted.Contains(value, StringComparison.Ordinal)),
            "OAuth code, state, verifier or token material was persisted in plaintext.");
        return assertions;
    }

    private static AuthorityDispatchContext Context(string tenant, string principal, string actor) =>
        new(tenant, principal, actor, "approval", "sealed-facts");
    private static OAuthTokenResponse Token(string access, string refresh, string scope, TimeSpan lifetime) =>
        new(access, refresh, lifetime, new HashSet<string>(StringComparer.Ordinal) { scope });
    private static string State(OAuthAuthorisationStart start) =>
        HttpUtility.ParseQueryString(start.AuthorisationUri.Query)["state"]!;

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class FakeTokenEndpoint : IHmrcOAuthTokenEndpoint
    {
        public OAuthTokenResponse ExchangeResponse { get; set; } = Token("a", "r", HmrcOAuthScopes.ReadVat, TimeSpan.FromHours(4));
        public OAuthTokenResponse RefreshResponse { get; set; } = Token("a2", "r2", HmrcOAuthScopes.ReadVat, TimeSpan.FromHours(4));
        public int ExchangeCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public string? LastCodeVerifier { get; private set; }
        public Uri? LastRedirectUri { get; private set; }
        public TimeSpan RefreshDelay { get; set; }
        public OAuthTokenEndpointException? RefreshException { get; set; }
        public ConcurrentQueue<string> RefreshTokens { get; } = new();

        public Task<OAuthTokenResponse> ExchangeCodeAsync(string clientId, string clientSecret, string code,
            string codeVerifier, Uri redirectUri, CancellationToken cancellationToken = default)
        {
            ExchangeCalls++;
            LastCodeVerifier = codeVerifier;
            LastRedirectUri = redirectUri;
            return Task.FromResult(ExchangeResponse);
        }

        public async Task<OAuthTokenResponse> RefreshAsync(string clientId, string clientSecret, string refreshToken,
            CancellationToken cancellationToken = default)
        {
            RefreshCalls++;
            RefreshTokens.Enqueue(refreshToken);
            if (RefreshDelay > TimeSpan.Zero) await Task.Delay(RefreshDelay, cancellationToken);
            if (RefreshException is not null) throw RefreshException;
            return RefreshResponse;
        }
    }
}
