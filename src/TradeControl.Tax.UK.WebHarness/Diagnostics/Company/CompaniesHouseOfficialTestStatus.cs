using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed record CompaniesHouseOfficialTestStatusRequest(
    string SubmissionNumber,
    string TransactionId,
    bool AuthorizeOfficialTestStatusQuery);

public sealed record CompaniesHouseOfficialTestStatusOutcome(
    string EvidenceClass,
    string SubmissionNumber,
    string TransactionId,
    string Endpoint,
    string ResponseKind,
    string RequestSha256,
    string ResponseSha256,
    string? AuthorityTransactionId,
    DateTimeOffset? GatewayTimestamp,
    [property: JsonConverter(typeof(JsonStringEnumConverter))]
    CompaniesHouseSubmissionStatusCode? Status,
    string? CompanyNumber,
    string? CustomerReference,
    string? ExaminerTelephone,
    string? ExaminerComment,
    IReadOnlyList<CompaniesHouseError> Errors,
    int ExternalRequestCount,
    bool RetryPermitted,
    bool StatusAcknowledgementRequired,
    bool TerminalFilingDecisionReceived);

public interface ICompaniesHouseOfficialTestStatus
{
    bool Enabled { get; }
    Task<CompaniesHouseOfficialTestStatusOutcome> ExecuteOnceAsync(
        CompaniesHouseOfficialTestStatusRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class CompaniesHouseOfficialTestStatus(
    ISecretProvider secrets,
    ICompaniesHouseXmlGatewaySession gateway,
    string protectedEvidenceRoot) : ICompaniesHouseOfficialTestStatus
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool Enabled => true;

    public async Task<CompaniesHouseOfficialTestStatusOutcome> ExecuteOnceAsync(
        CompaniesHouseOfficialTestStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.AuthorizeOfficialTestStatusQuery)
            throw new InvalidOperationException(
                "The request must explicitly authorize one Companies House official-test status query.");
        ValidateSubmissionNumber(request.SubmissionNumber);
        ValidateTransactionId(request.TransactionId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var filingDirectory = FindAcknowledgedFiling(request.SubmissionNumber);
            var statusDirectory = Path.Combine(filingDirectory, $"status-{request.TransactionId}");
            var outcomePath = Path.Combine(statusDirectory, "outcome.json");
            if (File.Exists(outcomePath))
                return await ReadOutcomeAsync(outcomePath, request, cancellationToken);

            EnsureTransactionIdIsUnique(request.TransactionId, statusDirectory);
            var responsePath = Path.Combine(statusDirectory, "response.xml");
            var protectedRequestPath = Path.Combine(statusDirectory, "request.protected.xml");
            if (File.Exists(responsePath) && File.Exists(protectedRequestPath))
            {
                var recoveredRequest = await File.ReadAllBytesAsync(protectedRequestPath, cancellationToken);
                var recoveredResponse = await File.ReadAllBytesAsync(responsePath, cancellationToken);
                var recovered = BuildOutcome(request, gateway.EvidenceClass,
                    recoveredRequest, recoveredResponse, 1);
                await WriteJsonAsync(outcomePath, recovered, cancellationToken);
                return recovered;
            }
            if (Directory.Exists(statusDirectory))
                throw new InvalidOperationException(
                    "A previous status query started without a recoverable response. Its transaction ID remains consumed and automatic retry is prohibited.");

            var materialized = await new CompaniesHouseEnvelopeMaterializer(secrets)
                .MaterializeStatusRequestTestAsync(CompaniesHouseStatusQueryScope.Submission,
                    request.TransactionId, request.SubmissionNumber, cancellationToken: cancellationToken);
            Directory.CreateDirectory(statusDirectory);
            await File.WriteAllBytesAsync(protectedRequestPath, materialized.Content.ToArray(), cancellationToken);
            await WriteJsonAsync(Path.Combine(statusDirectory, "pre-send.json"), new
            {
                evidenceClass = CompaniesHouseMaterializedGatewayRequest.MaterializedEvidence,
                request.SubmissionNumber,
                request.TransactionId,
                endpoint = CompaniesHouseGatewayEndpoints.OfficialTest,
                materialized.WireSha256,
                queryScope = CompaniesHouseStatusQueryScope.Submission.ToString(),
                externalRequestCount = 0,
                statusQueryAuthorized = true,
                statusAcknowledgementRequired = false
            }, cancellationToken);

            try
            {
                var response = await gateway.ExchangeAsync(new(
                    CompaniesHouseGatewayOperation.GetSubmissionStatus, materialized.Content), cancellationToken);
                response.Validate();
                await File.WriteAllBytesAsync(responsePath, response.Content.ToArray(), cancellationToken);
                var outcome = BuildOutcome(request, response.EvidenceClass,
                    materialized.Content.AsSpan(), response.Content.AsSpan(), 1, response.Kind);
                await WriteJsonAsync(outcomePath, outcome, cancellationToken);
                return outcome;
            }
            catch (CompaniesHouseUnclassifiedResponseException exception)
            {
                await File.WriteAllBytesAsync(Path.Combine(statusDirectory, "response.unclassified.xml"),
                    exception.ResponseContent.ToArray(), CancellationToken.None);
                await WriteFailureAsync(statusDirectory, request, materialized.WireSha256,
                    exception.ResponseSha256, exception, CancellationToken.None);
                throw new InvalidOperationException(
                    "The official status query returned an unclassified response. The protected raw response has been retained; the transaction ID remains consumed and automatic retry is prohibited.",
                    exception);
            }
            catch (Exception exception)
            {
                await WriteFailureAsync(statusDirectory, request, materialized.WireSha256,
                    null, exception, CancellationToken.None);
                throw new InvalidOperationException(
                    "The official status query did not produce a safely classified response. The transaction ID remains consumed and automatic retry is prohibited.",
                    exception);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string FindAcknowledgedFiling(string submissionNumber)
    {
        if (!Directory.Exists(protectedEvidenceRoot))
            throw new InvalidOperationException("The protected Companies House evidence root is unavailable.");
        var matches = Directory.EnumerateDirectories(protectedEvidenceRoot, $"*-{submissionNumber}",
                SearchOption.TopDirectoryOnly)
            .Where(path => File.Exists(Path.Combine(path, "outcome.json")))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException(
                "Exactly one retained filing acknowledgement is required for the requested submission.");

        var outcome = JsonSerializer.Deserialize<CompaniesHouseOfficialTestSubmissionOutcome>(
            File.ReadAllText(Path.Combine(matches[0], "outcome.json")), JsonOptions)
            ?? throw new InvalidDataException("The retained filing outcome is unreadable.");
        if (!string.Equals(outcome.SubmissionNumber, submissionNumber, StringComparison.Ordinal)
            || outcome.ResponseKind != CompaniesHouseGatewayResponseKind.Acknowledgement.ToString()
            || outcome.ExternalRequestCount != 1
            || outcome.RetryPermitted
            || outcome.TerminalFilingDecisionReceived
            || !IsSha256(outcome.RequestSha256)
            || !IsSha256(outcome.ResponseSha256))
            throw new InvalidOperationException(
                "The retained filing evidence is not an eligible non-terminal acknowledgement.");
        return matches[0];
    }

    private void EnsureTransactionIdIsUnique(string transactionId, string currentStatusDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(protectedEvidenceRoot, "*.json", SearchOption.AllDirectories))
        {
            if (Path.GetFullPath(path).StartsWith(Path.GetFullPath(currentStatusDirectory),
                    StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                var transaction = document.RootElement.EnumerateObject()
                    .FirstOrDefault(property => property.Name.Equals("TransactionId",
                        StringComparison.OrdinalIgnoreCase));
                if (transaction.Value.ValueKind == JsonValueKind.String
                    && string.Equals(transaction.Value.GetString(), transactionId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "The Companies House transaction ID is already present in retained evidence.");
            }
            catch (JsonException)
            {
                throw new InvalidDataException("Retained Companies House JSON evidence is malformed.");
            }
        }
    }

    private static CompaniesHouseOfficialTestStatusOutcome BuildOutcome(
        CompaniesHouseOfficialTestStatusRequest request,
        string evidenceClass,
        ReadOnlySpan<byte> requestContent,
        ReadOnlySpan<byte> responseContent,
        int externalRequestCount,
        CompaniesHouseGatewayResponseKind? classifiedKind = null)
    {
        var serializer = new CompaniesHouseEnvelopeSerializer();
        var acknowledgement = serializer.ParseGatewayAcknowledgement(responseContent);
        CompaniesHouseSubmissionStatus? status = null;
        var kind = classifiedKind;
        if (acknowledgement.Errors.Count > 0)
            kind ??= CompaniesHouseGatewayResponseKind.GatewayError;
        else
        {
            var statuses = serializer.ParseSubmissionStatuses(responseContent);
            status = statuses.SingleOrDefault(value => value.SubmissionNumber == request.SubmissionNumber)
                ?? throw new InvalidDataException(
                    "The Companies House response did not contain the requested submission status.");
            if (statuses.Count != 1)
                throw new InvalidDataException(
                    "A specific-submission query returned an unexpected number of statuses.");
            kind ??= CompaniesHouseGatewayResponseKind.SubmissionStatus;
        }
        if (kind is not (CompaniesHouseGatewayResponseKind.SubmissionStatus
            or CompaniesHouseGatewayResponseKind.GatewayError))
            throw new InvalidDataException("The Companies House status response kind is unexpected.");

        var statusCode = status?.StatusCode;
        return new(evidenceClass, request.SubmissionNumber, request.TransactionId,
            CompaniesHouseGatewayEndpoints.OfficialTest.ToString(), kind.Value.ToString(),
            Hash(requestContent), Hash(responseContent), acknowledgement.TransactionId,
            acknowledgement.GatewayTimestamp, statusCode, status?.CompanyNumber,
            status?.CustomerReference, status?.ExaminerTelephone, status?.ExaminerComment,
            status?.Errors ?? acknowledgement.Errors,
            externalRequestCount, false, false,
            statusCode is CompaniesHouseSubmissionStatusCode.Accept
                or CompaniesHouseSubmissionStatusCode.Reject
                or CompaniesHouseSubmissionStatusCode.InternalFailure);
    }

    private static async Task<CompaniesHouseOfficialTestStatusOutcome> ReadOutcomeAsync(
        string path, CompaniesHouseOfficialTestStatusRequest request, CancellationToken cancellationToken)
    {
        var outcome = JsonSerializer.Deserialize<CompaniesHouseOfficialTestStatusOutcome>(
            await File.ReadAllTextAsync(path, cancellationToken), JsonOptions)
            ?? throw new InvalidDataException("The retained status outcome is unreadable.");
        if (outcome.SubmissionNumber != request.SubmissionNumber
            || outcome.TransactionId != request.TransactionId
            || outcome.ExternalRequestCount != 1
            || outcome.RetryPermitted
            || outcome.StatusAcknowledgementRequired
            || !IsSha256(outcome.RequestSha256)
            || !IsSha256(outcome.ResponseSha256))
            throw new InvalidDataException("The retained status outcome failed validation.");
        return outcome;
    }

    private static Task WriteFailureAsync(string directory,
        CompaniesHouseOfficialTestStatusRequest request, string requestSha256,
        string? responseSha256, Exception exception, CancellationToken cancellationToken) =>
        WriteJsonAsync(Path.Combine(directory, "ambiguous-failure.json"), new
        {
            request.SubmissionNumber,
            request.TransactionId,
            endpoint = CompaniesHouseGatewayEndpoints.OfficialTest,
            requestSha256,
            responseSha256,
            externalRequestCount = 1,
            retryPermitted = false,
            exceptionType = exception.GetType().Name,
            message = exception.Message
        }, cancellationToken);

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, JsonOptions), cancellationToken);

    private static void ValidateSubmissionNumber(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 6
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException(
                "A six-character alphanumeric Companies House submission number is required.", nameof(value));
    }

    private static void ValidateTransactionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32
            || value.Any(character => !char.IsAsciiLetterOrDigit(character)))
            throw new ArgumentException(
                "A bounded alphanumeric Companies House transaction ID is required.", nameof(value));
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string Hash(ReadOnlySpan<byte> content) =>
        Convert.ToHexString(SHA256.HashData(content));
}

public sealed class DisabledCompaniesHouseOfficialTestStatus : ICompaniesHouseOfficialTestStatus
{
    public bool Enabled => false;

    public Task<CompaniesHouseOfficialTestStatusOutcome> ExecuteOnceAsync(
        CompaniesHouseOfficialTestStatusRequest request,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Companies House official-test status queries are disabled.");
}
