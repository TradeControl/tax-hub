using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;

public sealed record FraudHeaderOptions(TimeSpan MaximumContextAge, TimeSpan AllowedClockSkew,
    TimeSpan EvidenceRetention)
{
    public static FraudHeaderOptions Default { get; } = new(
        TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(1), TimeSpan.FromDays(30));
    public void Validate()
    {
        if (MaximumContextAge <= TimeSpan.Zero || MaximumContextAge > TimeSpan.FromHours(1))
            throw new ArgumentException("The fraud-context lifetime is invalid.");
        if (AllowedClockSkew < TimeSpan.Zero || AllowedClockSkew > TimeSpan.FromMinutes(5))
            throw new ArgumentException("The fraud-context clock skew is invalid.");
        if (EvidenceRetention < MaximumContextAge || EvidenceRetention > TimeSpan.FromDays(30))
            throw new ArgumentException("The protected fraud-evidence retention is invalid.");
    }
}

public sealed class FraudHeaderService : IDisposable
{
    private readonly FraudDeploymentTopology _topology;
    private readonly FraudVendorConfiguration _vendor;
    private readonly FraudHeaderOptions _options;
    private readonly IFraudContextStore _store;
    private readonly TimeProvider _clock;
    private readonly bool _sandboxValidatorDiagnostic;

    private FraudHeaderService(FraudDeploymentTopology topology, FraudVendorConfiguration vendor,
        FraudHeaderOptions options, IFraudContextStore store, TimeProvider? clock,
        bool sandboxValidatorDiagnostic = false)
    {
        _topology = topology ?? throw new ArgumentNullException(nameof(topology));
        _vendor = vendor ?? throw new ArgumentNullException(nameof(vendor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _clock = clock ?? TimeProvider.System;
        _sandboxValidatorDiagnostic = sandboxValidatorDiagnostic;
    }

    public static FraudHeaderService CreateFileBacked(string protectedRoot, ReadOnlySpan<byte> encryptionKey,
        FraudDeploymentTopology topology, FraudVendorConfiguration vendor,
        FraudHeaderOptions? options = null, TimeProvider? clock = null)
    {
        var selectedOptions = options ?? FraudHeaderOptions.Default;
        selectedOptions.Validate();
        var selectedClock = clock ?? TimeProvider.System;
        return new(topology, vendor, selectedOptions,
            new EncryptedFileFraudContextStore(protectedRoot, encryptionKey,
                selectedOptions.EvidenceRetention, selectedClock), selectedClock);
    }

    public static FraudHeaderService CreateFileBackedSandboxValidatorDiagnostic(string protectedRoot,
        ReadOnlySpan<byte> encryptionKey, FraudDeploymentTopology topology, FraudVendorConfiguration vendor,
        FraudHeaderOptions? options = null, TimeProvider? clock = null)
    {
        if (!topology.AllowsNonPublicDiagnosticAddresses)
            throw new ArgumentException("A sandbox-validator diagnostic topology is required.", nameof(topology));
        var selectedOptions = options ?? FraudHeaderOptions.Default;
        selectedOptions.Validate();
        var selectedClock = clock ?? TimeProvider.System;
        return new(topology, vendor, selectedOptions,
            new EncryptedFileFraudContextStore(protectedRoot, encryptionKey,
                selectedOptions.EvidenceRetention, selectedClock), selectedClock,
            sandboxValidatorDiagnostic: true);
    }

    public async Task<SealedFraudContextReference> CaptureAndSealAsync(FraudActorIdentity identity,
        BrowserFraudFacts browserFacts, TrustedIngressConnectionObservation ingress,
        CancellationToken cancellationToken = default)
    {
        var snapshot = FraudIngressCapture.Capture(identity, browserFacts, ingress, _topology, _vendor,
            _clock.GetUtcNow());
        return await _store.StoreAsync(snapshot, cancellationToken);
    }

    public async Task<FraudPreventionHeaders> BuildHeadersAsync(AuthorityDispatchContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        SealedFraudContextReference reference;
        try { reference = new(context.SealedClientFactsReference); }
        catch (ArgumentException) { throw new FraudContextRejectedException("The dispatch fraud-context reference is invalid."); }
        var identity = new FraudActorIdentity(context.TenantReference,
            context.AuthorisationPrincipalReference, context.ActorReference);
        var snapshot = await _store.ReadAsync(reference, identity, cancellationToken)
            ?? throw new FraudContextRejectedException("The sealed fraud context was not found for this actor.");
        if (snapshot.TopologyFingerprint != _topology.Fingerprint)
            throw new FraudContextRejectedException("The deployment topology changed after fraud-fact capture.");
        var now = _clock.GetUtcNow();
        if (snapshot.CapturedAtUtc > now + _options.AllowedClockSkew
            || snapshot.CapturedAtUtc < now - _options.MaximumContextAge)
            throw new FraudContextRejectedException("The sealed fraud context is stale or from the future.");
        return FraudHeaderFormatter.Format(snapshot, _sandboxValidatorDiagnostic);
    }

    public void Dispose() => _store.Dispose();
}
