using System.Collections.Concurrent;
using System.Text.Json;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;

namespace TradeControl.Tax.UK.Adapters.Submission.Audit;

public enum SubmissionAttemptState
{
    Reserved,
    Sending,
    Succeeded,
    Rejected,
    Failed,
    Unknown
}

public sealed record SubmissionAttemptReservation(
    string LogicalSubmissionReference,
    string OperationId,
    string Method,
    string TenantReference,
    string PrincipalReference,
    string SubjectPeriodReference,
    string? PreparedDigest,
    string? ApprovalReference,
    AuthorityEnvironment Environment);

public sealed record SubmissionAttemptOutcome(
    SubmissionAttemptState State,
    string OutcomeCode,
    int? ActualStatusCode = null,
    string? CorrelationReference = null,
    string? SafeResponseReference = null);

public sealed record SubmissionAttemptRecord(
    string AttemptReference,
    string LogicalSubmissionReference,
    string OperationId,
    string Method,
    string TenantReference,
    string PrincipalReference,
    string SubjectPeriodReference,
    string? PreparedDigest,
    string? ApprovalReference,
    AuthorityEnvironment Environment,
    SubmissionAttemptState State,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? OutcomeCode = null,
    int? ActualStatusCode = null,
    string? CorrelationReference = null,
    string? SafeResponseReference = null)
{
    public bool IsWrite => Method is not "GET" and not "HEAD";
    public bool IsActive => State is SubmissionAttemptState.Reserved
        or SubmissionAttemptState.Sending or SubmissionAttemptState.Unknown;
}

public sealed record SubmissionAttemptStoreOptions(
    string MetadataPath,
    TimeSpan Retention,
    int MaximumRecords = 100_000)
{
    public static SubmissionAttemptStoreOptions SevenYearMetadata(string metadataPath) =>
        new(metadataPath, TimeSpan.FromDays(365.25 * 7));
}

public sealed class ActiveSubmissionAttemptException(string attemptReference)
    : InvalidOperationException("An active attempt already exists for this logical submission.")
{
    public string AttemptReference { get; } = attemptReference;
}

public interface ISubmissionAttemptStore
{
    Task<SubmissionAttemptRecord> ReserveAsync(SubmissionAttemptReservation reservation,
        CancellationToken cancellationToken = default);
    Task<SubmissionAttemptRecord?> GetAsync(string tenantReference, string principalReference,
        string attemptReference, CancellationToken cancellationToken = default);
    Task<SubmissionAttemptRecord> RecordOutcomeAsync(string tenantReference, string principalReference,
        string attemptReference, SubmissionAttemptOutcome outcome, CancellationToken cancellationToken = default);
}

public sealed class FileSubmissionAttemptStore : ISubmissionAttemptStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private readonly SubmissionAttemptStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _fileLock;

    public FileSubmissionAttemptStore(SubmissionAttemptStoreOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.MetadataPath) || !Path.IsPathFullyQualified(options.MetadataPath))
            throw new ArgumentException("The attempt metadata path must be absolute.", nameof(options));
        if (options.Retention < TimeSpan.FromDays(1) || options.MaximumRecords < 1)
            throw new ArgumentException("Attempt retention and capacity must be positive and bounded.", nameof(options));

        _options = options with { MetadataPath = Path.GetFullPath(options.MetadataPath) };
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fileLock = FileLocks.GetOrAdd(_options.MetadataPath, _ => new SemaphoreSlim(1, 1));
    }

    public async Task<SubmissionAttemptRecord> ReserveAsync(SubmissionAttemptReservation reservation,
        CancellationToken cancellationToken = default)
    {
        ValidateReservation(reservation);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var records = await ReadAsync(cancellationToken);
            Prune(records);
            var method = reservation.Method.Trim().ToUpperInvariant();
            var isWrite = method is not "GET" and not "HEAD";
            var active = isWrite ? records.FirstOrDefault(item => item.IsWrite && item.IsActive
                && item.TenantReference == reservation.TenantReference
                && item.LogicalSubmissionReference == reservation.LogicalSubmissionReference) : null;
            if (active is not null) throw new ActiveSubmissionAttemptException(active.AttemptReference);
            if (records.Count >= _options.MaximumRecords)
                throw new InvalidOperationException("The bounded attempt store has reached its configured capacity.");

            var now = _timeProvider.GetUtcNow();
            var record = new SubmissionAttemptRecord(
                Guid.NewGuid().ToString("N"),
                reservation.LogicalSubmissionReference,
                reservation.OperationId,
                method,
                reservation.TenantReference,
                reservation.PrincipalReference,
                reservation.SubjectPeriodReference,
                reservation.PreparedDigest,
                reservation.ApprovalReference,
                reservation.Environment,
                SubmissionAttemptState.Reserved,
                now,
                now);
            records.Add(record);
            await WriteAsync(records, cancellationToken);
            return record;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<SubmissionAttemptRecord?> GetAsync(string tenantReference, string principalReference,
        string attemptReference, CancellationToken cancellationToken = default)
    {
        ValidateReference(tenantReference, nameof(tenantReference));
        ValidateReference(principalReference, nameof(principalReference));
        ValidateReference(attemptReference, nameof(attemptReference));
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var records = await ReadAsync(cancellationToken);
            return records.SingleOrDefault(item => item.AttemptReference == attemptReference
                && item.TenantReference == tenantReference && item.PrincipalReference == principalReference);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task<SubmissionAttemptRecord> RecordOutcomeAsync(string tenantReference, string principalReference,
        string attemptReference, SubmissionAttemptOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ValidateReference(tenantReference, nameof(tenantReference));
        ValidateReference(principalReference, nameof(principalReference));
        ValidateReference(attemptReference, nameof(attemptReference));
        ValidateOutcome(outcome);
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            await using var processLock = await AcquireProcessLockAsync(cancellationToken);
            var records = await ReadAsync(cancellationToken);
            var index = records.FindIndex(item => item.AttemptReference == attemptReference
                && item.TenantReference == tenantReference && item.PrincipalReference == principalReference);
            if (index < 0) throw new KeyNotFoundException("No attempt exists for the supplied tenant and principal.");
            if (!CanTransition(records[index].State, outcome.State))
                throw new InvalidOperationException("The requested attempt-state transition is not permitted.");
            var updated = records[index] with
            {
                State = outcome.State,
                UpdatedAt = _timeProvider.GetUtcNow(),
                OutcomeCode = outcome.OutcomeCode,
                ActualStatusCode = outcome.ActualStatusCode,
                CorrelationReference = outcome.CorrelationReference,
                SafeResponseReference = outcome.SafeResponseReference
            };
            records[index] = updated;
            await WriteAsync(records, cancellationToken);
            return updated;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    private async Task<List<SubmissionAttemptRecord>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_options.MetadataPath)) return [];
        await using var stream = new FileStream(_options.MetadataPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length == 0) return [];
        return await JsonSerializer.DeserializeAsync<List<SubmissionAttemptRecord>>(stream, JsonOptions,
            cancellationToken) ?? throw new InvalidDataException("The attempt metadata store is invalid.");
    }

    private async Task<FileStream> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.MetadataPath)!;
        Directory.CreateDirectory(directory);
        var lockPath = _options.MetadataPath + ".lock";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            }
        }
    }

    private async Task WriteAsync(List<SubmissionAttemptRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_options.MetadataPath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(_options.MetadataPath)}.{Guid.NewGuid():N}.tmp");
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
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private void Prune(List<SubmissionAttemptRecord> records)
    {
        var cutoff = _timeProvider.GetUtcNow() - _options.Retention;
        records.RemoveAll(item => !item.IsActive && item.UpdatedAt < cutoff);
    }

    private static bool CanTransition(SubmissionAttemptState current, SubmissionAttemptState next) => current switch
    {
        SubmissionAttemptState.Reserved => next is SubmissionAttemptState.Sending
            or SubmissionAttemptState.Succeeded or SubmissionAttemptState.Rejected
            or SubmissionAttemptState.Failed or SubmissionAttemptState.Unknown,
        SubmissionAttemptState.Sending => next is SubmissionAttemptState.Succeeded
            or SubmissionAttemptState.Rejected or SubmissionAttemptState.Failed or SubmissionAttemptState.Unknown,
        SubmissionAttemptState.Unknown => next is SubmissionAttemptState.Succeeded
            or SubmissionAttemptState.Rejected or SubmissionAttemptState.Failed or SubmissionAttemptState.Unknown,
        _ => false
    };

    private static void ValidateReservation(SubmissionAttemptReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        ValidateReference(reservation.LogicalSubmissionReference, nameof(reservation.LogicalSubmissionReference));
        ValidateReference(reservation.OperationId, nameof(reservation.OperationId));
        ValidateReference(reservation.Method, nameof(reservation.Method));
        ValidateReference(reservation.TenantReference, nameof(reservation.TenantReference));
        ValidateReference(reservation.PrincipalReference, nameof(reservation.PrincipalReference));
        ValidateReference(reservation.SubjectPeriodReference, nameof(reservation.SubjectPeriodReference));
        var method = reservation.Method.Trim().ToUpperInvariant();
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "DELETE"))
            throw new ArgumentException("The attempt method is not supported.", nameof(reservation));
        if (method is not "GET" and not "HEAD")
        {
            if (reservation.PreparedDigest is null || reservation.PreparedDigest.Length != 64
                || !reservation.PreparedDigest.All(Uri.IsHexDigit))
                throw new ArgumentException("A write attempt requires a SHA-256 prepared digest.", nameof(reservation));
            ValidateReference(reservation.ApprovalReference, nameof(reservation.ApprovalReference));
        }
    }

    private static void ValidateOutcome(SubmissionAttemptOutcome outcome)
    {
        ValidateReference(outcome.OutcomeCode, nameof(outcome.OutcomeCode));
        if (outcome.OutcomeCode.Length > 128)
            throw new ArgumentException("The outcome code is too long.", nameof(outcome));
        if (outcome.ActualStatusCode is < 100 or > 599)
            throw new ArgumentException("The actual HTTP status is invalid.", nameof(outcome));
        ValidateOptionalReference(outcome.CorrelationReference, 128, nameof(outcome.CorrelationReference));
        ValidateOptionalReference(outcome.SafeResponseReference, 256, nameof(outcome.SafeResponseReference));
        if (outcome.SafeResponseReference is not null
            && Uri.TryCreate(outcome.SafeResponseReference, UriKind.Absolute, out _))
            throw new ArgumentException("Authority-returned absolute URLs cannot be stored as response references.", nameof(outcome));
        if (outcome.SafeResponseReference is not null
            && (outcome.SafeResponseReference.StartsWith('/')
                || outcome.SafeResponseReference.Contains("..", StringComparison.Ordinal)
                || outcome.SafeResponseReference.Contains('\\')))
            throw new ArgumentException("A response reference must be an opaque store-relative identifier.", nameof(outcome));
    }

    private static void ValidateOptionalReference(string? value, int maximumLength, string parameterName)
    {
        if (value is not null && (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength))
            throw new ArgumentException("A stored reference is empty or exceeds its bound.", parameterName);
    }

    private static void ValidateReference(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException("A bounded record reference is required.", parameterName);
    }
}
