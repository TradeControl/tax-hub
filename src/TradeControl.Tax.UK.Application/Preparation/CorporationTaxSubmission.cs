using System.Collections.Immutable;

namespace TradeControl.Tax.UK.Application.Preparation;

public enum CorporationTaxSubmissionOperation
{
    Diagnostic,
    SubmitToAuthority
}

public sealed record CorporationTaxSubmissionCommand(
    CorporationTaxSubmissionOperation Operation,
    PreparedSubmissionPackage Package);

public enum CorporationTaxConversationState
{
    Pending,
    Accepted,
    Rejected,
    RecoveryRequired
}

public enum CorporationTaxDeletionState
{
    NotRequested,
    Pending,
    Acknowledged,
    Failed,
    Unknown
}

public sealed class CorporationTaxBusinessError
{
    public CorporationTaxBusinessError(string code, string message, string? location = null)
    {
        Code = CorporationTaxSubmissionOutcome.RequireBoundedText(code, 64, nameof(code));
        Message = CorporationTaxSubmissionOutcome.RequireBoundedText(message, 512, nameof(message));
        Location = CorporationTaxSubmissionOutcome.OptionalBoundedText(location, 256, nameof(location));
    }

    public string Code { get; }
    public string Message { get; }
    public string? Location { get; }
}

public sealed class CorporationTaxDeletionOutcome
{
    public CorporationTaxDeletionOutcome(CorporationTaxDeletionState state,
        string? safeEvidenceReference = null)
    {
        State = state;
        SafeEvidenceReference = CorporationTaxSubmissionOutcome.OptionalSafeReference(
            safeEvidenceReference, nameof(safeEvidenceReference));
    }

    public CorporationTaxDeletionState State { get; }
    public string? SafeEvidenceReference { get; }
}

public sealed class CorporationTaxSubmissionOutcome
{
    private CorporationTaxSubmissionOutcome(
        CorporationTaxConversationState state,
        string? correlationReference,
        string? safeReceiptReference,
        IEnumerable<CorporationTaxBusinessError>? errors,
        CorporationTaxDeletionOutcome deletion)
    {
        State = state;
        CorrelationReference = OptionalSafeReference(correlationReference, nameof(correlationReference));
        SafeReceiptReference = OptionalSafeReference(safeReceiptReference, nameof(safeReceiptReference));
        Errors = errors?.ToImmutableArray() ?? [];
        Deletion = deletion ?? throw new ArgumentNullException(nameof(deletion));

        if (state == CorporationTaxConversationState.Pending && CorrelationReference is null)
            throw new ArgumentException("A pending Corporation Tax conversation requires a safe correlation reference.");
        if (state == CorporationTaxConversationState.Accepted && SafeReceiptReference is null)
            throw new ArgumentException("An accepted Corporation Tax conversation requires safe receipt evidence.");
        if (state == CorporationTaxConversationState.Rejected && Errors.IsDefaultOrEmpty)
            throw new ArgumentException("A rejected Corporation Tax conversation requires bounded business error evidence.");
        if (Errors.Length > 50)
            throw new ArgumentException("Corporation Tax business error evidence is limited to 50 entries.", nameof(errors));
        if (state != CorporationTaxConversationState.Rejected && !Errors.IsDefaultOrEmpty)
            throw new ArgumentException("Business rejection errors are valid only for a rejected conversation.");
    }

    public CorporationTaxConversationState State { get; }
    public string? CorrelationReference { get; }
    public string? SafeReceiptReference { get; }
    public ImmutableArray<CorporationTaxBusinessError> Errors { get; }
    public CorporationTaxDeletionOutcome Deletion { get; }

    public static CorporationTaxSubmissionOutcome Pending(string correlationReference) => new(
        CorporationTaxConversationState.Pending, correlationReference, null, null,
        new(CorporationTaxDeletionState.NotRequested));

    public static CorporationTaxSubmissionOutcome Accepted(string safeReceiptReference,
        string? correlationReference = null, CorporationTaxDeletionOutcome? deletion = null) => new(
        CorporationTaxConversationState.Accepted, correlationReference, safeReceiptReference, null,
        deletion ?? new(CorporationTaxDeletionState.NotRequested));

    public static CorporationTaxSubmissionOutcome Rejected(IEnumerable<CorporationTaxBusinessError> errors,
        string? correlationReference = null, CorporationTaxDeletionOutcome? deletion = null) => new(
        CorporationTaxConversationState.Rejected, correlationReference, null, errors,
        deletion ?? new(CorporationTaxDeletionState.NotRequested));

    public static CorporationTaxSubmissionOutcome RecoveryRequired(string? correlationReference = null,
        CorporationTaxDeletionOutcome? deletion = null) => new(
        CorporationTaxConversationState.RecoveryRequired, correlationReference, null, null,
        deletion ?? new(CorporationTaxDeletionState.Unknown));

    internal static string RequireBoundedText(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException($"A non-empty value of at most {maximumLength} printable characters is required.", parameterName);
        return value;
    }

    internal static string? OptionalBoundedText(string? value, int maximumLength, string parameterName) =>
        value is null ? null : RequireBoundedText(value, maximumLength, parameterName);

    internal static string? OptionalSafeReference(string? value, string parameterName)
    {
        if (value is null) return null;
        var result = RequireBoundedText(value, 256, parameterName);
        if (Uri.TryCreate(result, UriKind.Absolute, out _))
            throw new ArgumentException("Authority evidence must be represented by a safe local reference, not a URL.", parameterName);
        return result;
    }
}

public abstract class CorporationTaxSubmissionGateway
{
    private const string CorporationTaxServiceCode = "HMRC-CORPORATION-TAX";

    public Task<CorporationTaxSubmissionOutcome> SendAsync(CorporationTaxSubmissionCommand command,
        AuthorityDispatchContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Package);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var package = command.Package;
        if (!string.Equals(package.ServiceCode, CorporationTaxServiceCode, StringComparison.Ordinal))
            throw new InvalidOperationException("The package is not an HMRC Corporation Tax service package.");
        if (command.Operation != CorporationTaxSubmissionOperation.SubmitToAuthority)
            throw new InvalidOperationException("A diagnostic Corporation Tax operation cannot be dispatched.");
        if (package.Polling.Mode != SubmissionPollingMode.TransactionEngine
            || package.Polling.RelativeStatusPath is not null)
            throw new InvalidOperationException("Corporation Tax requires Transaction Engine polling without a REST-style status path.");

        var artifacts = package.Documents.Select(document => document.Artifact).Prepend(package.Transmission).ToArray();
        if (artifacts.Select(artifact => artifact.Status).Distinct().Count() != 1)
            throw new InvalidOperationException("A mixed-status Corporation Tax package cannot be dispatched.");
        if (artifacts.Any(artifact => artifact.Status != PreparedArtifactStatus.SubmissionReady))
            throw new InvalidOperationException("Every Corporation Tax artifact must be submission-ready.");
        if (artifacts.Any(artifact => artifact.HasErrors))
            throw new InvalidOperationException("A Corporation Tax package has blocking preparation findings.");

        // Phase 5.8 must introduce and bind the specifically validated CT service-artifact identity.
        // No identity available in Phase 5.7 is authorised, even if callers relabel diagnostic bytes.
        throw new InvalidOperationException(
            "No Corporation Tax service artifact is approved for authority dispatch.");
    }

    protected abstract Task<CorporationTaxSubmissionOutcome> SendEligibleAsync(
        CorporationTaxSubmissionCommand command, AuthorityDispatchContext context,
        CancellationToken cancellationToken);
}
