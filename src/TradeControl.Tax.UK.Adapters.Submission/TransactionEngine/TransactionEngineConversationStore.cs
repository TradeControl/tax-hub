using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

internal enum TransactionEngineConversationStage
{
    Prepared,
    SubmissionUncertain,
    AwaitingPoll,
    TerminalPersisted,
    DeletePending,
    Complete,
    RecoveryRequired
}

internal sealed record TransactionEngineConversationRecord(
    string ConversationReference,
    string TenantReference,
    string PrincipalReference,
    string LogicalSubmissionReference,
    string TransactionId,
    string ServiceRootSha256,
    string WireSha256,
    string PayloadReference,
    TransactionEngineConversationStage Stage,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? CorrelationId = null,
    string? ResponseEndpoint = null,
    int? PollIntervalSeconds = null,
    DateTimeOffset? PollNotBefore = null,
    string? TerminalResponseReference = null,
    string? TerminalResponseSha256 = null,
    string? ReceiptReference = null,
    string? Outcome = null,
    string? RecoveryReason = null,
    IReadOnlyList<TransactionEngineResponseEvidence>? ResponseEvidence = null)
{
    internal bool MaySubmit => Stage == TransactionEngineConversationStage.Prepared;
    internal bool RequiresDataRequest => Stage == TransactionEngineConversationStage.SubmissionUncertain;
    internal bool MayPoll(DateTimeOffset now) => Stage == TransactionEngineConversationStage.AwaitingPoll
        && PollNotBefore <= now;
    internal bool MayDelete => Stage == TransactionEngineConversationStage.TerminalPersisted
        && TerminalResponseReference is not null && TerminalResponseSha256 is not null;
}

internal sealed record TransactionEngineResponseEvidence(
    string SafeResponseReference,
    string Sha256,
    DateTimeOffset RecordedAt);

internal sealed record TransactionEngineConversationStoreOptions(
    string MetadataPath,
    int MaximumRecords = 10_000);

internal sealed class FileTransactionEngineConversationStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly TransactionEngineConversationStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lock;

    internal FileTransactionEngineConversationStore(TransactionEngineConversationStoreOptions options,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.MetadataPath) || !Path.IsPathFullyQualified(options.MetadataPath)
            || options.MaximumRecords is < 1 or > 100_000)
            throw new ArgumentException("The Transaction Engine metadata configuration is invalid.", nameof(options));
        _options = options with { MetadataPath = Path.GetFullPath(options.MetadataPath) };
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lock = Locks.GetOrAdd(_options.MetadataPath, _ => new SemaphoreSlim(1, 1));
    }

    internal Task<TransactionEngineConversationRecord> CreateAsync(
        string tenantReference, string principalReference, string logicalSubmissionReference,
        TransactionEngineMaterializedSubmission submission, string payloadReference,
        CancellationToken cancellationToken = default) => MutateAsync(records =>
    {
        ValidateReference(tenantReference, nameof(tenantReference));
        ValidateReference(principalReference, nameof(principalReference));
        ValidateReference(logicalSubmissionReference, nameof(logicalSubmissionReference));
        ValidateReference(payloadReference, nameof(payloadReference));
        if (records.Any(record => record.TenantReference == tenantReference
                && record.LogicalSubmissionReference == logicalSubmissionReference
                && record.Stage != TransactionEngineConversationStage.Complete))
            throw new InvalidOperationException("An incomplete Transaction Engine conversation already exists.");
        if (records.Count >= _options.MaximumRecords)
            throw new InvalidOperationException("The Transaction Engine conversation store is full.");
        var now = _timeProvider.GetUtcNow();
        var created = new TransactionEngineConversationRecord(
            Guid.NewGuid().ToString("N"), tenantReference, principalReference,
            logicalSubmissionReference, submission.TransactionId, submission.ServiceRootSha256,
            submission.WireSha256,
            payloadReference, TransactionEngineConversationStage.Prepared, now, now);
        records.Add(created);
        return created;
    }, cancellationToken);

    internal Task<TransactionEngineConversationRecord?> GetAsync(string tenantReference,
        string principalReference, string conversationReference,
        CancellationToken cancellationToken = default) => InspectAsync(records =>
    {
        ValidateReference(tenantReference, nameof(tenantReference));
        ValidateReference(principalReference, nameof(principalReference));
        ValidateReference(conversationReference, nameof(conversationReference));
        return records.SingleOrDefault(record => record.ConversationReference == conversationReference
            && record.TenantReference == tenantReference && record.PrincipalReference == principalReference);
    }, cancellationToken);

    internal Task<TransactionEngineConversationRecord> BeginSubmissionAsync(string tenant, string principal,
        string reference, CancellationToken cancellationToken = default) => TransitionAsync(
            tenant, principal, reference, TransactionEngineConversationStage.Prepared,
            current => current with { Stage = TransactionEngineConversationStage.SubmissionUncertain },
            cancellationToken);

    internal Task<TransactionEngineConversationRecord> RecordAcknowledgementAsync(string tenant, string principal,
        string reference, TransactionEngineParsedResponse response,
        CancellationToken cancellationToken = default)
    {
        if (response.Kind != TransactionEngineResponseKind.Acknowledgement
            || response.CorrelationId is null || response.ResponseEndpoint is null
            || response.PollIntervalSeconds is null)
            throw new ArgumentException("A valid acknowledgement is required.", nameof(response));
        return TransitionAsync(tenant, principal, reference,
            TransactionEngineConversationStage.SubmissionUncertain,
            current => current with
            {
                Stage = TransactionEngineConversationStage.AwaitingPoll,
                CorrelationId = response.CorrelationId,
                ResponseEndpoint = response.ResponseEndpoint.AbsoluteUri,
                PollIntervalSeconds = response.PollIntervalSeconds,
                PollNotBefore = _timeProvider.GetUtcNow().AddSeconds(response.PollIntervalSeconds.Value)
            }, cancellationToken);
    }

    internal Task<TransactionEngineConversationRecord> RecordRecoveredCorrelationAsync(
        string tenant, string principal, string reference, string correlationId, Uri responseEndpoint,
        int pollIntervalSeconds, CancellationToken cancellationToken = default)
    {
        TransactionEngineWire.ValidateCorrelationId(correlationId);
        if (responseEndpoint.Scheme != Uri.UriSchemeHttps || pollIntervalSeconds is < 1 or > 86_400)
            throw new ArgumentException("Recovered polling information is invalid.");
        return TransitionAsync(tenant, principal, reference,
            TransactionEngineConversationStage.SubmissionUncertain,
            current => current with
            {
                Stage = TransactionEngineConversationStage.AwaitingPoll,
                CorrelationId = correlationId,
                ResponseEndpoint = responseEndpoint.AbsoluteUri,
                PollIntervalSeconds = pollIntervalSeconds,
                PollNotBefore = _timeProvider.GetUtcNow().AddSeconds(pollIntervalSeconds)
            }, cancellationToken);
    }

    internal Task<TransactionEngineConversationRecord> RecordPollAcknowledgementAsync(
        string tenant, string principal, string reference, TransactionEngineParsedResponse response,
        CancellationToken cancellationToken = default) => TransitionAsync(
            tenant, principal, reference, TransactionEngineConversationStage.AwaitingPoll,
            current =>
            {
                EnsureSameCorrelation(current, response);
                if (response.Kind != TransactionEngineResponseKind.Acknowledgement
                    || response.ResponseEndpoint is null || response.PollIntervalSeconds is null)
                    throw new ArgumentException("A valid poll acknowledgement is required.", nameof(response));
                return current with
                {
                    ResponseEndpoint = response.ResponseEndpoint.AbsoluteUri,
                    PollIntervalSeconds = response.PollIntervalSeconds,
                    PollNotBefore = _timeProvider.GetUtcNow().AddSeconds(response.PollIntervalSeconds.Value)
                };
            }, cancellationToken);

    internal Task<TransactionEngineConversationRecord> RecordTerminalAsync(
        string tenant, string principal, string reference, TransactionEngineParsedResponse response,
        string terminalResponseReference, string? receiptReference,
        CancellationToken cancellationToken = default) => TransitionAsync(
            tenant, principal, reference, TransactionEngineConversationStage.AwaitingPoll,
            current =>
            {
                EnsureSameCorrelation(current, response);
                if (response.Kind is not (TransactionEngineResponseKind.BusinessResponse
                    or TransactionEngineResponseKind.BusinessError))
                    throw new ArgumentException("A terminal business response or error is required.", nameof(response));
                ValidateReference(terminalResponseReference, nameof(terminalResponseReference));
                if (response.ContainsIrmarkReceipt && string.IsNullOrWhiteSpace(receiptReference))
                    throw new InvalidOperationException("The signed IRmark receipt must be retained before deletion.");
                if (receiptReference is not null) ValidateReference(receiptReference, nameof(receiptReference));
                return current with
                {
                    Stage = TransactionEngineConversationStage.TerminalPersisted,
                    TerminalResponseReference = terminalResponseReference,
                    TerminalResponseSha256 = response.ResponseSha256,
                    ReceiptReference = receiptReference,
                    Outcome = response.Kind.ToString()
                };
            }, cancellationToken);

    internal Task<TransactionEngineConversationRecord> BeginDeleteAsync(string tenant, string principal,
        string reference, CancellationToken cancellationToken = default) => TransitionAsync(
            tenant, principal, reference, TransactionEngineConversationStage.TerminalPersisted,
            current => current.MayDelete
                ? current with { Stage = TransactionEngineConversationStage.DeletePending }
                : throw new InvalidOperationException("Terminal evidence must be persisted before delete."),
            cancellationToken);

    internal Task<TransactionEngineConversationRecord> RecordDeleteResponseAsync(string tenant, string principal,
        string reference, TransactionEngineParsedResponse response,
        CancellationToken cancellationToken = default) => TransitionAsync(
            tenant, principal, reference, TransactionEngineConversationStage.DeletePending,
            current =>
            {
                EnsureSameCorrelation(current, response);
                if (response.Kind != TransactionEngineResponseKind.DeleteResponse)
                    throw new ArgumentException("A delete response is required.", nameof(response));
                return current with { Stage = TransactionEngineConversationStage.Complete };
            }, cancellationToken);

    internal Task<TransactionEngineConversationRecord> RequireRecoveryAsync(string tenant, string principal,
        string reference, string reason, CancellationToken cancellationToken = default) => MutateOwnedAsync(
            tenant, principal, reference, current =>
            {
                if (current.Stage == TransactionEngineConversationStage.Complete)
                    throw new InvalidOperationException("A completed conversation cannot enter recovery.");
                ValidateReference(reason, nameof(reason));
                return current with
                {
                    Stage = TransactionEngineConversationStage.RecoveryRequired,
                    RecoveryReason = reason
                };
            }, cancellationToken);

    internal Task<TransactionEngineConversationRecord> RecordGatewayErrorAsync(string tenant, string principal,
        string reference, TransactionEngineParsedResponse response, string responseReference,
        CancellationToken cancellationToken = default) => MutateOwnedAsync(tenant, principal, reference, current =>
    {
        if (current.Stage is not (TransactionEngineConversationStage.SubmissionUncertain
            or TransactionEngineConversationStage.AwaitingPoll))
            throw new InvalidOperationException("A gateway error is not valid at this conversation stage.");
        if (response.Kind != TransactionEngineResponseKind.GatewayError)
            throw new ArgumentException("A parsed gateway error is required.", nameof(response));
        ValidateReference(responseReference, nameof(responseReference));
        return current with
        {
            Stage = TransactionEngineConversationStage.RecoveryRequired,
            TerminalResponseReference = responseReference,
            TerminalResponseSha256 = response.ResponseSha256,
            Outcome = response.Kind.ToString(),
            RecoveryReason = "TRANSACTION_ENGINE_GATEWAY_ERROR"
        };
    }, cancellationToken);

    internal Task<TransactionEngineConversationRecord> RecordResponseEvidenceAsync(
        string tenant, string principal, string reference, string responseReference, string responseSha256,
        CancellationToken cancellationToken = default) => MutateOwnedAsync(tenant, principal, reference, current =>
    {
        ValidateReference(responseReference, nameof(responseReference));
        if (responseSha256.Length != 64 || responseSha256.Any(character =>
                !(character is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException("A SHA-256 response digest is required.", nameof(responseSha256));
        var evidence = current.ResponseEvidence?.ToImmutableArray()
            ?? ImmutableArray<TransactionEngineResponseEvidence>.Empty;
        if (evidence.Length >= 10_000)
            throw new InvalidOperationException("The bounded response-evidence history is full.");
        return current with
        {
            ResponseEvidence = evidence.Add(new(responseReference, responseSha256, _timeProvider.GetUtcNow()))
        };
    }, cancellationToken);

    private Task<TransactionEngineConversationRecord> TransitionAsync(string tenant, string principal,
        string reference, TransactionEngineConversationStage expected,
        Func<TransactionEngineConversationRecord, TransactionEngineConversationRecord> transition,
        CancellationToken cancellationToken) => MutateOwnedAsync(tenant, principal, reference, current =>
    {
        if (current.Stage != expected)
            throw new InvalidOperationException($"Conversation stage {current.Stage} does not permit this operation.");
        return transition(current);
    }, cancellationToken);

    private Task<TransactionEngineConversationRecord> MutateOwnedAsync(string tenant, string principal,
        string reference, Func<TransactionEngineConversationRecord, TransactionEngineConversationRecord> mutation,
        CancellationToken cancellationToken) => MutateAsync(records =>
    {
        ValidateReference(tenant, nameof(tenant));
        ValidateReference(principal, nameof(principal));
        ValidateReference(reference, nameof(reference));
        var index = records.FindIndex(record => record.ConversationReference == reference
            && record.TenantReference == tenant && record.PrincipalReference == principal);
        if (index < 0) throw new KeyNotFoundException("No Transaction Engine conversation exists for this owner.");
        var updated = mutation(records[index]) with { UpdatedAt = _timeProvider.GetUtcNow() };
        records[index] = updated;
        return updated;
    }, cancellationToken);

    private async Task<T> InspectAsync<T>(Func<List<TransactionEngineConversationRecord>, T> inspect,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try { return inspect(await ReadAsync(cancellationToken)); }
        finally { _lock.Release(); }
    }

    private async Task<T> MutateAsync<T>(Func<List<TransactionEngineConversationRecord>, T> mutate,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var records = await ReadAsync(cancellationToken);
            var result = mutate(records);
            await WriteAsync(records, cancellationToken);
            return result;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<TransactionEngineConversationRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.MetadataPath)) return [];
        await using var stream = new FileStream(_options.MetadataPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<List<TransactionEngineConversationRecord>>(
            stream, JsonOptions, cancellationToken) ?? [];
    }

    private async Task WriteAsync(List<TransactionEngineConversationRecord> records,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_options.MetadataPath)!);
        var temporary = _options.MetadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            File.Move(temporary, _options.MetadataPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var lockPath = _options.MetadataPath + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 99)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
            }
        }
        throw new IOException("Could not acquire the Transaction Engine metadata lock.");
    }

    private static void EnsureSameCorrelation(TransactionEngineConversationRecord current,
        TransactionEngineParsedResponse response)
    {
        if (current.CorrelationId is null || response.CorrelationId != current.CorrelationId)
            throw new InvalidDataException("The response correlation ID does not match the conversation.");
    }

    private static void ValidateReference(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("A bounded conversation reference is required.", parameterName);
    }
}
