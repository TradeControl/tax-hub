using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeControl.Tax.UK.Adapters.Submission.Audit;

public sealed record SubmissionLogEvent(
    string OperationId,
    string State,
    string AttemptReference,
    string TenantReference,
    string PrincipalReference,
    string? CorrelationReference = null,
    string? OutcomeCode = null);

public sealed class SubmissionLogger(TextWriter writer)
{
    public SubmissionLogger() : this(TextWriter.Null) { }

    public Task LogAsync(string operationType, string status, CancellationToken cancellationToken = default) =>
        LogAsync(new(operationType, status, "legacy-runner", "legacy-runner", "legacy-runner"), cancellationToken);

    public async Task LogAsync(SubmissionLogEvent entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        cancellationToken.ThrowIfCancellationRequested();
        var safe = new
        {
            operation = RedactedClassification(entry.OperationId, 128),
            state = RedactedClassification(entry.State, 64),
            attempt = Fingerprint(entry.AttemptReference),
            tenant = Fingerprint(entry.TenantReference),
            principal = Fingerprint(entry.PrincipalReference),
            correlation = entry.CorrelationReference is null ? null : Fingerprint(entry.CorrelationReference),
            outcome = entry.OutcomeCode is null ? null : RedactedClassification(entry.OutcomeCode, 128)
        };
        await writer.WriteLineAsync(JsonSerializer.Serialize(safe).AsMemory(), cancellationToken);
    }

    private static string RedactedClassification(string value, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new ArgumentException("A log classification is empty or exceeds its bound.", nameof(value));
        var sensitive = new[] { "bearer", "token", "secret", "password", "credential", "client-id",
            "client_secret", "gov-client", "gov-vendor", "fraud" };
        if (sensitive.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase)))
            return "[REDACTED]";
        return value;
    }

    private static string Fingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException("A log reference is empty or exceeds its bound.", nameof(value));
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"ref:{Convert.ToHexString(digest.AsSpan(0, 8))}";
    }
}
