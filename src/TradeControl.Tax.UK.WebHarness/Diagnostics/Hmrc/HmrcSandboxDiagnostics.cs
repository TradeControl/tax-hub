using System.Net;
using System.Security.Cryptography;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Hmrc;

public sealed record BrowserFraudCapture(
    string JavascriptUserAgent,
    Guid DeviceId,
    IReadOnlyList<FraudScreen> Screens,
    string Timezone,
    FraudWindowSize WindowSize);

public sealed record CollectedFraudSessionFacts(
    BrowserFraudFacts Browser,
    IPAddress ClientAddress,
    int ClientPort,
    IPAddress ServerAddress);

public sealed record HmrcSandboxActor(string PrincipalReference, string ActorReference);

public sealed record FraudHeaderDiagnosticOutcome(
    HmrcFraudHeaderValidationResponse? Response,
    OAuthReauthorisationReason? ReauthorisationReason)
{
    public bool RequiresAuthorisation => ReauthorisationReason.HasValue;
}

public interface IHmrcSandboxDiagnostics
{
    bool Enabled { get; }
    Task<OAuthAuthorisationStart> BeginAuthorisationAsync(HmrcSandboxActor actor,
        CancellationToken cancellationToken = default);
    Task<OAuthAccessOutcome> CompleteCallbackAsync(HmrcSandboxActor actor, OAuthCallback callback,
        CancellationToken cancellationToken = default);
    Task DisconnectAsync(HmrcSandboxActor actor, CancellationToken cancellationToken = default);
    Task<FraudHeaderDiagnosticOutcome> ValidateAsync(HmrcSandboxActor actor, CollectedFraudSessionFacts facts,
        CancellationToken cancellationToken = default);
}

public sealed class HmrcSandboxDiagnostics : IHmrcSandboxDiagnostics, IDisposable
{
    private const string Tenant = "web-harness-tenant";
    private readonly string _fraudRoot;
    private readonly byte[] _fraudKey;
    private readonly HmrcOAuthService _oauth;
    private readonly HmrcOAuthTokenEndpoint _tokenEndpoint;
    private readonly HmrcFraudHeaderValidator _validator;
    private readonly IReadOnlyDictionary<string, string> _licenseIds;

    private HmrcSandboxDiagnostics(string fraudRoot, byte[] fraudKey, HmrcOAuthService oauth,
        HmrcOAuthTokenEndpoint tokenEndpoint, HmrcFraudHeaderValidator validator,
        IReadOnlyDictionary<string, string> licenseIds)
    {
        _fraudRoot = fraudRoot;
        _fraudKey = fraudKey;
        _oauth = oauth;
        _tokenEndpoint = tokenEndpoint;
        _validator = validator;
        _licenseIds = licenseIds;
    }

    public bool Enabled => true;

    public static HmrcSandboxDiagnostics Create(string contentRoot, IConfiguration configuration)
    {
        var configuredPath = configuration["TaxHub:HmrcSandbox:ClientSettingsPath"];
        var settingsPath = Path.GetFullPath(Path.Combine(contentRoot,
            configuredPath ?? "../../../../.local/vat_mtd_client_test-master/mtd-client-vat/clientsettings.json"));
        var configuredRuntimePath = configuration["TaxHub:HmrcSandbox:RuntimeStorePath"];
        var runtimeRoot = Path.GetFullPath(Path.Combine(contentRoot,
            configuredRuntimePath ?? "../../../../.local/tax-hub/web-harness"));
        Directory.CreateDirectory(runtimeRoot);
        var oauthKey = LoadOrCreateDevelopmentKey(Path.Combine(runtimeRoot, "oauth-store.key"));
        var fraudKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var secrets = new JsonFileSecretProvider(settingsPath,
                HmrcSecretReferences.LegacyVatSandboxJsonProperties);
            var environment = EnvironmentSelector.Sandbox();
            var tokenEndpoint = new HmrcOAuthTokenEndpoint(environment);
            var oauth = HmrcOAuthService.CreateFileBackedSandbox(Path.Combine(runtimeRoot, "oauth-grants.enc"),
                oauthKey, secrets, tokenEndpoint);
            var licenseIds = configuration.GetSection("TaxHub:HmrcSandbox:LicenseIds").GetChildren()
                .ToDictionary(item => item.Key, item => item.Value ?? string.Empty, StringComparer.Ordinal);
            return new(Path.Combine(runtimeRoot, "fraud-contexts"), fraudKey, oauth, tokenEndpoint,
                new HmrcFraudHeaderValidator(environment), licenseIds);
        }
        finally { CryptographicOperations.ZeroMemory(oauthKey); }
    }

    internal static byte[] LoadOrCreateDevelopmentKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("The development key path must be absolute.", nameof(path));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path)) return ReadDevelopmentKey(path);

        var generated = RandomNumberGenerator.GetBytes(32);
        try
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(generated);
                stream.Flush(flushToDisk: true);
                return generated;
            }
            catch (IOException) when (File.Exists(path))
            {
                CryptographicOperations.ZeroMemory(generated);
                return ReadDevelopmentKey(path);
            }
        }
        catch
        {
            CryptographicOperations.ZeroMemory(generated);
            throw;
        }
    }

    private static byte[] ReadDevelopmentKey(string path)
    {
        var key = File.ReadAllBytes(path);
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new InvalidOperationException("The development OAuth-store key has an invalid length.");
        }
        return key;
    }

    public Task<OAuthAuthorisationStart> BeginAuthorisationAsync(HmrcSandboxActor actor,
        CancellationToken cancellationToken = default) =>
        _oauth.BeginAuthorisationAsync(OAuthContext(actor), HmrcOAuthScopes.ReadVat, cancellationToken);

    public Task<OAuthAccessOutcome> CompleteCallbackAsync(HmrcSandboxActor actor, OAuthCallback callback,
        CancellationToken cancellationToken = default) =>
        _oauth.CompleteCallbackAsync(OAuthContext(actor), callback, cancellationToken);

    public Task DisconnectAsync(HmrcSandboxActor actor, CancellationToken cancellationToken = default) =>
        _oauth.RevokeAsync(OAuthContext(actor), HmrcOAuthScopes.ReadVat, cancellationToken);

    public async Task<FraudHeaderDiagnosticOutcome> ValidateAsync(HmrcSandboxActor actor,
        CollectedFraudSessionFacts facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentNullException.ThrowIfNull(facts);
        var identity = ActorIdentity(actor);
        using var access = await _oauth.GetAccessAsync(OAuthContext(actor), HmrcOAuthScopes.ReadVat,
            cancellationToken);
        if (access.Kind != OAuthAccessOutcomeKind.Available)
            return new(null, access.ReauthorisationReason);

        var topology = FraudDeploymentTopology.SandboxValidatorDiagnosticDirect(
            "web-harness-observed-direct", facts.ServerAddress);
        var version = typeof(HmrcSandboxDiagnostics).Assembly.GetName().Version?.ToString(3) ?? "development";
        var vendor = new FraudVendorConfiguration("Trade Control Tax Hub",
            new Dictionary<string, string> { ["tax-hub-web-harness"] = version }, _licenseIds);
        using var fraud = FraudHeaderService.CreateFileBackedSandboxValidatorDiagnostic(
            _fraudRoot, _fraudKey, topology, vendor);
        var reference = await fraud.CaptureAndSealAsync(identity, facts.Browser,
            new TrustedIngressConnectionObservation(facts.ClientAddress, facts.ClientPort,
                DateTimeOffset.UtcNow), cancellationToken);
        var dispatch = new AuthorityDispatchContext(Tenant, actor.PrincipalReference, actor.ActorReference,
            "web-harness-validator", reference.Value);
        var headers = await fraud.BuildHeadersAsync(dispatch, cancellationToken);
        var response = await _validator.ValidateAsync(headers, access.AccessToken!, cancellationToken);
        return new(response, null);
    }

    private static FraudActorIdentity ActorIdentity(HmrcSandboxActor actor) =>
        new FraudActorIdentity(Tenant, actor.PrincipalReference, actor.ActorReference).Validate();

    private static AuthorityDispatchContext OAuthContext(HmrcSandboxActor actor)
    {
        var identity = ActorIdentity(actor);
        return new(identity.TenantReference, identity.PrincipalReference, identity.ActorReference,
            "web-harness-oauth", "not-used-for-oauth");
    }

    public void Dispose()
    {
        _validator.Dispose();
        _oauth.Dispose();
        _tokenEndpoint.Dispose();
        CryptographicOperations.ZeroMemory(_fraudKey);
    }
}

public sealed class DisabledHmrcSandboxDiagnostics : IHmrcSandboxDiagnostics
{
    public bool Enabled => false;
    public Task<OAuthAuthorisationStart> BeginAuthorisationAsync(HmrcSandboxActor actor,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("HMRC sandbox diagnostics are disabled.");
    public Task<OAuthAccessOutcome> CompleteCallbackAsync(HmrcSandboxActor actor, OAuthCallback callback,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("HMRC sandbox diagnostics are disabled.");
    public Task DisconnectAsync(HmrcSandboxActor actor, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("HMRC sandbox diagnostics are disabled.");
    public Task<FraudHeaderDiagnosticOutcome> ValidateAsync(HmrcSandboxActor actor,
        CollectedFraudSessionFacts facts,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("HMRC sandbox diagnostics are disabled.");
}
