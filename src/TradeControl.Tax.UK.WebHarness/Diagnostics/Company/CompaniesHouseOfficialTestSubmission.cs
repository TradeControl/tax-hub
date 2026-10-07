using System.Text.Json;
using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public interface ICompaniesHouseOfficialTestSubmission
{
    bool Enabled { get; }
    Task<CompaniesHouseOfficialTestSubmissionOutcome> ExecuteOnceAsync(
        CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed record CompaniesHouseOfficialTestSubmissionOutcome(
    string EvidenceClass,
    string SubmissionNumber,
    string TransactionId,
    string Endpoint,
    string ResponseKind,
    string RequestSha256,
    string ResponseSha256,
    string? AuthorityTransactionId,
    DateTimeOffset? GatewayTimestamp,
    IReadOnlyList<CompaniesHouseError> Errors,
    string? EventLogCode,
    bool EventLogRecorded,
    int ExternalRequestCount,
    bool RetryPermitted,
    bool TerminalFilingDecisionReceived);

public sealed class CompaniesHouseOfficialTestSubmission(
    ICompaniesHouseAccountsRunner accountsRunner,
    ISecretProvider secrets,
    ICompaniesHouseXmlGatewaySession gateway,
    ICompaniesHouseSubmissionEventLog eventLog,
    string protectedEvidenceRoot) : ICompaniesHouseOfficialTestSubmission
{
    private int _sendStarted;
    private CompaniesHouseOfficialTestSubmissionOutcome? _completed;

    public bool Enabled => true;

    public async Task<CompaniesHouseOfficialTestSubmissionOutcome> ExecuteOnceAsync(
        CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (!payload.Filing.AuthorizeOfficialTestSubmission)
            throw new InvalidOperationException(
                "The request must explicitly authorize one Companies House official-test submission.");
        if (_completed is not null) return _completed;

        var package = await accountsRunner.PrepareAsync(payload, cancellationToken);
        var materialized = await new CompaniesHouseEnvelopeMaterializer(secrets)
            .MaterializeTestAsync(package, payload.Filing.TransactionId, cancellationToken);
        var attemptDirectory = Path.Combine(protectedEvidenceRoot,
            $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{payload.Filing.SubmissionNumber}");
        Directory.CreateDirectory(attemptDirectory);
        await File.WriteAllBytesAsync(Path.Combine(attemptDirectory, "request.protected.xml"),
            materialized.Content.ToArray(), cancellationToken);
        await WriteJsonAsync(Path.Combine(attemptDirectory, "pre-send.json"), new
        {
            evidenceClass = CompaniesHouseMaterializedEnvelope.MaterializedEvidence,
            payload.Filing.SubmissionNumber,
            payload.Filing.TransactionId,
            endpoint = CompaniesHouseGatewayEndpoints.OfficialTest,
            materialized.PreviewSha256,
            materialized.AccountsSha256,
            materialized.WireSha256,
            externalRequestCount = 0,
            sendAuthorized = true
        }, cancellationToken);

        if (Interlocked.CompareExchange(ref _sendStarted, 1, 0) != 0)
            throw new InvalidOperationException(
                "This process has already started its single Companies House official-test submission.");

        try
        {
            var response = await gateway.ExchangeAsync(new(CompaniesHouseGatewayOperation.FormSubmission,
                materialized.Content), cancellationToken);
            response.Validate();
            await File.WriteAllBytesAsync(Path.Combine(attemptDirectory, "response.xml"),
                response.Content.ToArray(), cancellationToken);

            var acknowledgement = new CompaniesHouseEnvelopeSerializer()
                .ParseGatewayAcknowledgement(response.Content.AsSpan());
            var isError = response.Kind != CompaniesHouseGatewayResponseKind.Acknowledgement
                || acknowledgement.Errors.Count > 0;
            string? eventLogCode = null;
            try
            {
                eventLogCode = await eventLog.WriteAsync(payload.SqlConnection, package,
                    payload.Filing.SubmissionNumber, payload.Filing.TransactionId,
                    response.Kind.ToString(), response.RequestSha256, response.ResponseSha256,
                    acknowledgement.Errors, isError, cancellationToken);
            }
            catch (Exception exception)
            {
                await WriteJsonAsync(Path.Combine(attemptDirectory, "event-log-failure.json"), new
                {
                    payload.Filing.SubmissionNumber,
                    eventLogRecorded = false,
                    exceptionType = exception.GetType().Name,
                    message = exception.Message
                }, CancellationToken.None);
            }
            _completed = new(gateway.EvidenceClass, payload.Filing.SubmissionNumber,
                payload.Filing.TransactionId, CompaniesHouseGatewayEndpoints.OfficialTest.ToString(),
                response.Kind.ToString(), response.RequestSha256, response.ResponseSha256,
                acknowledgement.TransactionId, acknowledgement.GatewayTimestamp,
                acknowledgement.Errors, eventLogCode, eventLogCode is not null, 1, false, false);
            await WriteJsonAsync(Path.Combine(attemptDirectory, "outcome.json"), _completed,
                cancellationToken);
            return _completed;
        }
        catch (CompaniesHouseUnclassifiedResponseException exception)
        {
            await File.WriteAllBytesAsync(Path.Combine(attemptDirectory, "response.unclassified.xml"),
                exception.ResponseContent.ToArray(), CancellationToken.None);
            var eventLogCode = await TryWriteFailureEventAsync(payload, package, materialized.WireSha256,
                exception.ResponseSha256, exception, attemptDirectory);
            await WriteJsonAsync(Path.Combine(attemptDirectory, "ambiguous-failure.json"), new
            {
                payload.Filing.SubmissionNumber,
                payload.Filing.TransactionId,
                endpoint = CompaniesHouseGatewayEndpoints.OfficialTest,
                materialized.WireSha256,
                exception.ResponseSha256,
                responsePreserved = true,
                externalRequestCount = 1,
                retryPermitted = false,
                eventLogCode,
                eventLogRecorded = eventLogCode is not null,
                exceptionType = exception.GetType().Name,
                message = exception.Message
            }, CancellationToken.None);
            throw new InvalidOperationException(
                "The official test exchange returned an unclassified response. The protected raw response has been retained; the one-send gate remains consumed and automatic retry is prohibited."
                + EventLogSuffix(eventLogCode),
                exception);
        }
        catch (Exception exception)
        {
            var eventLogCode = await TryWriteFailureEventAsync(payload, package, materialized.WireSha256,
                null, exception, attemptDirectory);
            await WriteJsonAsync(Path.Combine(attemptDirectory, "ambiguous-failure.json"), new
            {
                payload.Filing.SubmissionNumber,
                payload.Filing.TransactionId,
                endpoint = CompaniesHouseGatewayEndpoints.OfficialTest,
                materialized.WireSha256,
                externalRequestCount = 1,
                retryPermitted = false,
                eventLogCode,
                eventLogRecorded = eventLogCode is not null,
                exceptionType = exception.GetType().Name,
                message = exception.Message
            }, CancellationToken.None);
            throw new InvalidOperationException(
                "The official test exchange did not produce a safely classified response. The one-send gate remains consumed and automatic retry is prohibited."
                + EventLogSuffix(eventLogCode),
                exception);
        }
    }

    private async Task<string?> TryWriteFailureEventAsync(CompaniesHouseAccountsPayload payload,
        PreparedSubmissionPackage package, string requestSha256, string? responseSha256,
        Exception exchangeException, string attemptDirectory)
    {
        try
        {
            return await eventLog.WriteAsync(payload.SqlConnection, package, payload.Filing.SubmissionNumber,
                payload.Filing.TransactionId, "ExchangeFailure", requestSha256, responseSha256,
                [new CompaniesHouseError("EXCHANGE", exchangeException.Message, null)], true,
                CancellationToken.None);
        }
        catch (Exception logException)
        {
            await WriteJsonAsync(Path.Combine(attemptDirectory, "event-log-failure.json"), new
            {
                payload.Filing.SubmissionNumber,
                eventLogRecorded = false,
                exceptionType = logException.GetType().Name,
                message = logException.Message
            }, CancellationToken.None);
            return null;
        }
    }

    private static string EventLogSuffix(string? eventLogCode) => eventLogCode is null
        ? " The Trade Control event log could not be written; protected evidence remains authoritative."
        : $" Trade Control event log: {eventLogCode}.";

    private static Task WriteJsonAsync(string path, object value, CancellationToken cancellationToken) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            WriteIndented = true
        }), cancellationToken);
}

public sealed class DisabledCompaniesHouseOfficialTestSubmission : ICompaniesHouseOfficialTestSubmission
{
    public bool Enabled => false;

    public Task<CompaniesHouseOfficialTestSubmissionOutcome> ExecuteOnceAsync(
        CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Companies House official-test submission is disabled.");
}
