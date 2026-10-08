using System.Collections.Immutable;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

namespace TradeControl.Tax.UK.Application.Preparation;

public enum CompaniesHouseConversationState
{
    Prepared,
    Acknowledged,
    GatewayRejected,
    Pending,
    Parked,
    Accepted,
    Rejected,
    InternalFailure
}

/// <summary>
/// Safe local evidence produced by the development simulator. Official gateway evidence uses the
/// separately classified Submission-adapter exchange records and must never be represented by this type.
/// </summary>
public sealed class CompaniesHouseSimulationEvidence
{
    public const string EvidenceClass = "SIMULATED — NOT FILED";

    public CompaniesHouseSimulationEvidence(string safeReference, string requestSha256, string responseSha256)
    {
        SafeReference = RequireSafeReference(safeReference, nameof(safeReference));
        RequestSha256 = RequireSha256(requestSha256, nameof(requestSha256));
        ResponseSha256 = RequireSha256(responseSha256, nameof(responseSha256));
    }

    public string Class => EvidenceClass;
    public string SafeReference { get; }
    public string RequestSha256 { get; }
    public string ResponseSha256 { get; }

    private static string RequireSafeReference(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)
            || Uri.TryCreate(value, UriKind.Absolute, out _))
            throw new ArgumentException("A bounded local evidence reference is required.", parameterName);
        return value;
    }

    internal static string RequireSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64
            || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A SHA-256 value must contain exactly 64 hexadecimal characters.", parameterName);
        return value.ToUpperInvariant();
    }
}

/// <summary>
/// Immutable, transport-neutral state for one Companies House filing conversation. It does not
/// send, poll, persist or materialise credentials; those remain adapter and product-host responsibilities.
/// </summary>
public sealed class CompaniesHouseFilingConversation
{
    private CompaniesHouseFilingConversation(string submissionNumber, string transmissionSha256,
        string accountsSha256, CompaniesHouseConversationState state,
        IEnumerable<CompaniesHouseError>? errors,
        IEnumerable<CompaniesHouseSimulationEvidence>? evidence,
        bool statusAcknowledgementRequired, bool statusAcknowledged)
    {
        if (string.IsNullOrWhiteSpace(submissionNumber) || submissionNumber.Length != 6
            || submissionNumber.Any(char.IsControl))
            throw new ArgumentException("A six-character Companies House submission number is required.", nameof(submissionNumber));
        SubmissionNumber = submissionNumber;
        TransmissionSha256 = CompaniesHouseSimulationEvidence.RequireSha256(transmissionSha256, nameof(transmissionSha256));
        AccountsSha256 = CompaniesHouseSimulationEvidence.RequireSha256(accountsSha256, nameof(accountsSha256));
        State = state;
        Errors = errors?.Select(ValidateError).ToImmutableArray() ?? [];
        Evidence = evidence?.ToImmutableArray() ?? [];
        StatusAcknowledgementRequired = statusAcknowledgementRequired;
        StatusAcknowledged = statusAcknowledged;

        if (Errors.Length > 50)
            throw new ArgumentException("Companies House error evidence is limited to 50 entries.", nameof(errors));
        if (state is CompaniesHouseConversationState.Rejected or CompaniesHouseConversationState.GatewayRejected
            && Errors.IsDefaultOrEmpty)
            throw new ArgumentException("A rejected filing requires bounded error evidence.", nameof(errors));
        if (state is not (CompaniesHouseConversationState.Rejected or CompaniesHouseConversationState.GatewayRejected)
            && !Errors.IsDefaultOrEmpty)
            throw new ArgumentException("Error evidence is valid only for a rejected filing.", nameof(errors));
        if (statusAcknowledged && !statusAcknowledgementRequired)
            throw new ArgumentException("A status cannot be acknowledged when no acknowledgement is required.");
    }

    public string SubmissionNumber { get; }
    public string TransmissionSha256 { get; }
    public string AccountsSha256 { get; }
    public CompaniesHouseConversationState State { get; }
    public ImmutableArray<CompaniesHouseError> Errors { get; }
    public ImmutableArray<CompaniesHouseSimulationEvidence> Evidence { get; }
    public bool StatusAcknowledgementRequired { get; }
    public bool StatusAcknowledged { get; }
    public bool IsTerminal => State is CompaniesHouseConversationState.Accepted
        or CompaniesHouseConversationState.GatewayRejected
        or CompaniesHouseConversationState.Rejected or CompaniesHouseConversationState.InternalFailure;

    public static CompaniesHouseFilingConversation Start(string submissionNumber,
        string transmissionSha256, string accountsSha256) => new(submissionNumber, transmissionSha256,
        accountsSha256, CompaniesHouseConversationState.Prepared, null, null, false, false);

    public CompaniesHouseFilingConversation ApplyAcknowledgement(
        CompaniesHouseGatewayAcknowledgement acknowledgement, CompaniesHouseSimulationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(acknowledgement);
        ArgumentNullException.ThrowIfNull(evidence);
        if (State != CompaniesHouseConversationState.Prepared)
            throw new InvalidOperationException("A gateway acknowledgement may be applied only to a prepared filing.");

        var errors = acknowledgement.Errors ?? throw new ArgumentException(
            "Gateway acknowledgement errors cannot be null.", nameof(acknowledgement));
        return errors.Count == 0
            ? Copy(CompaniesHouseConversationState.Acknowledged, null, evidence, false, false)
            : Copy(CompaniesHouseConversationState.GatewayRejected, errors, evidence, false, false);
    }

    public CompaniesHouseFilingConversation ApplyStatus(CompaniesHouseSubmissionStatus status,
        bool acknowledgementRequired, CompaniesHouseSimulationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(evidence);
        if (State == CompaniesHouseConversationState.Prepared)
            throw new InvalidOperationException("A status cannot precede the gateway acknowledgement.");
        if (IsTerminal)
            throw new InvalidOperationException("A terminal Companies House result cannot be replaced.");
        if (!string.Equals(status.SubmissionNumber, SubmissionNumber, StringComparison.Ordinal))
            throw new InvalidOperationException("The status belongs to a different Companies House submission.");

        var next = status.StatusCode switch
        {
            CompaniesHouseSubmissionStatusCode.Accept => CompaniesHouseConversationState.Accepted,
            CompaniesHouseSubmissionStatusCode.Reject => CompaniesHouseConversationState.Rejected,
            CompaniesHouseSubmissionStatusCode.Pending => CompaniesHouseConversationState.Pending,
            CompaniesHouseSubmissionStatusCode.Parked => CompaniesHouseConversationState.Parked,
            CompaniesHouseSubmissionStatusCode.InternalFailure => CompaniesHouseConversationState.InternalFailure,
            _ => throw new InvalidOperationException("The Companies House status is unsupported.")
        };
        return Copy(next, status.Errors, evidence, acknowledgementRequired, false);
    }

    public CompaniesHouseFilingConversation AcknowledgeStatus(CompaniesHouseSimulationEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (!StatusAcknowledgementRequired)
            throw new InvalidOperationException("The selected status query does not require StatusAck.");
        if (StatusAcknowledged)
            throw new InvalidOperationException("The selected status result has already been acknowledged.");
        return Copy(State, Errors, evidence, true, true);
    }

    private CompaniesHouseFilingConversation Copy(CompaniesHouseConversationState state,
        IEnumerable<CompaniesHouseError>? errors, CompaniesHouseSimulationEvidence evidence,
        bool acknowledgementRequired, bool acknowledged) => new(SubmissionNumber, TransmissionSha256,
        AccountsSha256, state, errors, Evidence.Add(evidence), acknowledgementRequired, acknowledged);

    private static CompaniesHouseError ValidateError(CompaniesHouseError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(RequireBounded(error.Code, 64, nameof(error.Code)),
            RequireBounded(error.Message, 512, nameof(error.Message)),
            error.Location is null ? null : RequireBounded(error.Location, 256, nameof(error.Location)));
    }

    private static string RequireBounded(string value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException($"A non-empty value of at most {maximumLength} printable characters is required.", parameterName);
        return value;
    }
}
