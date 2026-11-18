using System.Security.Cryptography;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

public enum CorporationTaxExternalTestOperation
{
    Recover,
    Poll,
    Delete
}

public sealed record CorporationTaxExternalTestConversationOutcome(
    string EvidenceClass,
    string ConversationReference,
    string TransactionId,
    string ServiceArtifactSha256,
    string WireSha256,
    string Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PollNotBefore,
    string? Outcome,
    IReadOnlyList<string> ResponseSha256,
    bool ProductionEnabled);

/// <summary>
/// Development/test facade over the durable CT Transaction Engine state machine.
/// Staging performs no HTTP. Every other method performs at most one exchange and
/// requires the caller to repeat the bounded authorisation reference for the named attempt.
/// </summary>
public sealed class CorporationTaxExternalTestConversation : IDisposable
{
    public const string Evidence = "HMRC ETS CONVERSATION — PROTECTED TEST EVIDENCE";

    private readonly ISecretProvider _secrets;
    private readonly string _product;
    private readonly string _productVersion;
    private readonly FileTransactionEngineConversationStore _store;
    private readonly TransactionEngineConversationCoordinator _conversations;
    private readonly TransactionEngineExchangeCoordinator _exchanges;
    private readonly HttpClient _http;

    public CorporationTaxExternalTestConversation(ISecretProvider secrets, string protectedEvidenceRoot,
        string product, string productVersion, TimeProvider? timeProvider = null)
        : this(secrets, protectedEvidenceRoot, product, productVersion,
            TransactionEngineHttpGateway.CreateExternalTestClient(), timeProvider)
    {
    }

    internal CorporationTaxExternalTestConversation(ISecretProvider secrets, string protectedEvidenceRoot,
        string product, string productVersion, HttpClient http, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(http);
        if (string.IsNullOrWhiteSpace(protectedEvidenceRoot)
            || !Path.IsPathFullyQualified(protectedEvidenceRoot))
            throw new ArgumentException("An absolute protected evidence root is required.",
                nameof(protectedEvidenceRoot));
        if (!BoundedReference(product) || !BoundedReference(productVersion))
            throw new ArgumentException("Bounded product identity is required.");

        _secrets = secrets;
        _product = product;
        _productVersion = productVersion;
        var root = Path.GetFullPath(protectedEvidenceRoot);
        Directory.CreateDirectory(root);
        _store = new(new(Path.Combine(root, "conversations.json")), timeProvider);
        var content = new FileSubmissionContentStore(new(Path.Combine(root, "content"),
            MaximumPayloadBytes: 8 * 1024 * 1024, MaximumResponseBytes: 8 * 1024 * 1024));
        var protocol = TransactionEngineProtocolOptions.CorporationTaxExternalTest();
        _conversations = new(_store, content, protocol, timeProvider);
        _http = http;
        _exchanges = new(_conversations,
            new TransactionEngineHttpGateway(_http, protocol, new()));
    }

    public async Task<CorporationTaxExternalTestConversationOutcome> StageAsync(
        PreparedSubmissionPackage package, string transactionId, string tenantReference,
        string principalReference, string logicalSubmissionReference,
        CancellationToken cancellationToken = default)
    {
        var identity = await ResolveIdentityAsync(cancellationToken);
        var record = await _conversations.PrepareAsync(package, identity, transactionId,
            tenantReference, principalReference, logicalSubmissionReference, cancellationToken);
        return Safe(record);
    }

    public async Task<CorporationTaxExternalTestConversationOutcome> SubmitOnceAsync(
        string tenantReference, string principalReference, string conversationReference,
        string expectedTransactionId, string expectedServiceArtifactSha256, string expectedWireSha256,
        string attemptAuthorisationReference, CancellationToken cancellationToken = default)
    {
        RequireAuthorisation(attemptAuthorisationReference);
        var current = await RequiredAsync(tenantReference, principalReference, conversationReference,
            cancellationToken);
        if (current.Stage != TransactionEngineConversationStage.Prepared
            || !string.Equals(current.TransactionId, expectedTransactionId, StringComparison.Ordinal)
            || !FixedSha256(current.ServiceRootSha256, expectedServiceArtifactSha256)
            || !FixedSha256(current.WireSha256, expectedWireSha256))
            throw new InvalidOperationException(
                "The named ETS authorisation does not match the staged transaction and exact artifacts.");
        return Safe(await _exchanges.SubmitOnceAsync(tenantReference, principalReference,
            conversationReference, cancellationToken));
    }

    public async Task<CorporationTaxExternalTestConversationOutcome> AdvanceOnceAsync(
        string tenantReference, string principalReference, string conversationReference,
        CorporationTaxExternalTestOperation operation, string attemptAuthorisationReference,
        CancellationToken cancellationToken = default)
    {
        RequireAuthorisation(attemptAuthorisationReference);
        var record = operation switch
        {
            CorporationTaxExternalTestOperation.Recover => await _exchanges.RecoverOnceAsync(
                tenantReference, principalReference, conversationReference,
                await ResolveIdentityAsync(cancellationToken), cancellationToken),
            CorporationTaxExternalTestOperation.Poll => await _exchanges.PollOnceAsync(
                tenantReference, principalReference, conversationReference, cancellationToken),
            CorporationTaxExternalTestOperation.Delete => await _exchanges.DeleteOnceAsync(
                tenantReference, principalReference, conversationReference, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return Safe(record);
    }

    public async Task<CorporationTaxExternalTestConversationOutcome> ReadAsync(
        string tenantReference, string principalReference, string conversationReference,
        CancellationToken cancellationToken = default) =>
        Safe(await RequiredAsync(tenantReference, principalReference, conversationReference,
            cancellationToken));

    public void Dispose() => _http.Dispose();

    private async Task<TransactionEngineConversationRecord> RequiredAsync(string tenant, string principal,
        string conversation, CancellationToken cancellationToken) =>
        await _store.GetAsync(tenant, principal, conversation, cancellationToken)
        ?? throw new KeyNotFoundException("No CT ETS conversation exists for this tenant and principal.");

    private async Task<TransactionEngineSubmissionIdentity> ResolveIdentityAsync(
        CancellationToken cancellationToken)
    {
        using var vendor = await _secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.VendorId, cancellationToken);
        using var sender = await _secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.SenderId, cancellationToken);
        using var password = await _secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.Password, cancellationToken);
        using var utr = await _secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.Utr, cancellationToken);
        return new(Copy(vendor), Copy(sender), Copy(password), Copy(utr), _product, _productVersion);
    }

    private static CorporationTaxExternalTestConversationOutcome Safe(
        TransactionEngineConversationRecord record) => new(Evidence, record.ConversationReference,
        record.TransactionId, record.ServiceRootSha256, record.WireSha256, record.Stage.ToString(),
        record.CreatedAt, record.UpdatedAt, record.PollNotBefore, record.Outcome,
        record.ResponseEvidence?.Select(value => value.Sha256).ToArray() ?? [], false);

    private static void RequireAuthorisation(string value)
    {
        if (!BoundedReference(value))
            throw new InvalidOperationException(
                "A bounded named-attempt authorisation reference is required for this ETS exchange.");
    }

    private static bool BoundedReference(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && value.All(character => !char.IsControl(character));

    private static bool FixedSha256(string left, string right)
    {
        if (left.Length != 64 || right.Length != 64
            || !left.All(Uri.IsHexDigit) || !right.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
    }

    private static string Copy(ProtectedSecret secret)
    {
        var characters = new char[secret.Length];
        try
        {
            secret.CopyTo(characters);
            return new string(characters);
        }
        finally
        {
            Array.Clear(characters);
        }
    }
}
