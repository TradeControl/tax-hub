using System.Collections.Immutable;
using System.Security.Cryptography;
using TradeControl.Tax.UK.Adapters.Submission.Audit;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

internal enum TransactionEngineOutboundKind
{
    Submit,
    Poll,
    Delete,
    DataRequest
}

internal sealed record TransactionEngineOutboundMessage(
    TransactionEngineOutboundKind Kind,
    Uri Endpoint,
    ImmutableArray<byte> Content,
    string Sha256);

/// <summary>
/// Offline coordinator for recording-handler fixtures. It deliberately owns no
/// network client and is not registered by a host.
/// </summary>
internal sealed class TransactionEngineFixtureHandler(
    FileTransactionEngineConversationStore conversations,
    ISubmissionContentStore content,
    TransactionEngineProtocolOptions options,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal async Task<TransactionEngineConversationRecord> PrepareAsync(
        PreparedSubmissionPackage package, TransactionEngineSubmissionIdentity identity,
        string transactionId, string tenant, string principal, string logicalSubmissionReference,
        CancellationToken cancellationToken = default)
    {
        var submission = TransactionEngineWire.Materialize(package, identity, transactionId, options);
        var payloadReference = await content.StoreAsync(tenant, principal, SubmissionContentKind.Payload,
            submission.Content.ToArray(), cancellationToken);
        return await conversations.CreateAsync(tenant, principal, logicalSubmissionReference,
            submission, payloadReference, cancellationToken);
    }

    internal async Task<TransactionEngineOutboundMessage> BeginSubmissionAsync(
        string tenant, string principal, string conversationReference,
        CancellationToken cancellationToken = default)
    {
        var record = await conversations.BeginSubmissionAsync(tenant, principal,
            conversationReference, cancellationToken);
        var payload = await RequiredContentAsync(tenant, principal, record.PayloadReference,
            record.WireSha256, cancellationToken);
        return Outbound(TransactionEngineOutboundKind.Submit, options.SubmissionEndpoint, payload);
    }

    internal async Task<TransactionEngineOutboundMessage> BuildDataRequestAsync(
        string tenant, string principal, string conversationReference,
        TransactionEngineSubmissionIdentity identity, CancellationToken cancellationToken = default)
    {
        var record = await RequiredConversationAsync(tenant, principal, conversationReference, cancellationToken);
        if (!record.RequiresDataRequest)
            throw new InvalidOperationException("DATA_REQUEST is only available for an uncertain submission.");
        return Outbound(TransactionEngineOutboundKind.DataRequest, options.SubmissionEndpoint,
            TransactionEngineWire.DataRequest(identity, options));
    }

    internal async Task<TransactionEngineConversationRecord> ApplyDataResponseAsync(
        string tenant, string principal, string conversationReference, ReadOnlyMemory<byte> response,
        CancellationToken cancellationToken = default)
    {
        var record = await RequiredConversationAsync(tenant, principal, conversationReference, cancellationToken);
        if (!record.RequiresDataRequest)
            throw new InvalidOperationException("The conversation does not require DATA_REQUEST recovery.");
        _ = await PreserveResponseAsync(tenant, principal, conversationReference,
            response, cancellationToken);
        var parsed = TransactionEngineResponseParser.Parse(response.Span, options);
        var status = TransactionEngineResponseParser.MatchRecoveryRecord(parsed, record.TransactionId);
        return await conversations.RecordRecoveredCorrelationAsync(tenant, principal, conversationReference,
            status.CorrelationId, options.SubmissionEndpoint, options.RecoveryPollIntervalSeconds,
            cancellationToken);
    }

    internal async Task<TransactionEngineOutboundMessage> BuildPollAsync(
        string tenant, string principal, string conversationReference,
        CancellationToken cancellationToken = default)
    {
        var record = await RequiredConversationAsync(tenant, principal, conversationReference, cancellationToken);
        if (!record.MayPoll(_timeProvider.GetUtcNow()) || record.CorrelationId is null
            || record.ResponseEndpoint is null)
            throw new InvalidOperationException("The conversation is not yet eligible for polling.");
        var endpoint = TransactionEngineWire.ValidateResponseEndpoint(record.ResponseEndpoint, options);
        return Outbound(TransactionEngineOutboundKind.Poll, endpoint,
            TransactionEngineWire.Poll(record.CorrelationId, options));
    }

    internal async Task<TransactionEngineConversationRecord> ApplySubmissionResponseAsync(
        string tenant, string principal, string conversationReference, ReadOnlyMemory<byte> response,
        CancellationToken cancellationToken = default)
    {
        var responseReference = await PreserveResponseAsync(tenant, principal, conversationReference,
            response, cancellationToken);
        var parsed = TransactionEngineResponseParser.Parse(response.Span, options);
        var current = await RequiredConversationAsync(tenant, principal, conversationReference, cancellationToken);
        return parsed.Kind switch
        {
            TransactionEngineResponseKind.Acknowledgement
                when current.Stage == TransactionEngineConversationStage.SubmissionUncertain =>
                await conversations.RecordAcknowledgementAsync(tenant, principal, conversationReference,
                    parsed, cancellationToken),
            TransactionEngineResponseKind.Acknowledgement
                when current.Stage == TransactionEngineConversationStage.AwaitingPoll =>
                await conversations.RecordPollAcknowledgementAsync(tenant, principal, conversationReference,
                    parsed, cancellationToken),
            TransactionEngineResponseKind.BusinessResponse or TransactionEngineResponseKind.BusinessError
                when current.Stage == TransactionEngineConversationStage.AwaitingPoll =>
                await conversations.RecordTerminalAsync(tenant, principal, conversationReference, parsed,
                    responseReference, parsed.ContainsIrmarkReceipt ? responseReference : null, cancellationToken),
            TransactionEngineResponseKind.GatewayError =>
                await conversations.RecordGatewayErrorAsync(tenant, principal, conversationReference,
                    parsed, responseReference, cancellationToken),
            _ => throw new InvalidOperationException("The response is not valid for the current conversation stage.")
        };
    }

    internal async Task<TransactionEngineOutboundMessage> BeginOrResumeDeleteAsync(
        string tenant, string principal, string conversationReference,
        CancellationToken cancellationToken = default)
    {
        var record = await RequiredConversationAsync(tenant, principal, conversationReference, cancellationToken);
        if (record.Stage == TransactionEngineConversationStage.TerminalPersisted)
            record = await conversations.BeginDeleteAsync(tenant, principal, conversationReference,
                cancellationToken);
        if (record.Stage != TransactionEngineConversationStage.DeletePending || record.CorrelationId is null
            || record.ResponseEndpoint is null)
            throw new InvalidOperationException("The conversation is not eligible for delete.");
        var endpoint = TransactionEngineWire.ValidateResponseEndpoint(record.ResponseEndpoint, options);
        return Outbound(TransactionEngineOutboundKind.Delete, endpoint,
            TransactionEngineWire.Delete(record.CorrelationId, options));
    }

    internal async Task<TransactionEngineConversationRecord> ApplyDeleteResponseAsync(
        string tenant, string principal, string conversationReference, ReadOnlyMemory<byte> response,
        CancellationToken cancellationToken = default)
    {
        _ = await PreserveResponseAsync(tenant, principal, conversationReference,
            response, cancellationToken);
        var parsed = TransactionEngineResponseParser.Parse(response.Span, options);
        return await conversations.RecordDeleteResponseAsync(tenant, principal, conversationReference,
            parsed, cancellationToken);
    }

    private async Task<TransactionEngineConversationRecord> RequiredConversationAsync(
        string tenant, string principal, string reference, CancellationToken cancellationToken) =>
        await conversations.GetAsync(tenant, principal, reference, cancellationToken)
        ?? throw new KeyNotFoundException("No Transaction Engine conversation exists for this owner.");

    private async Task<byte[]> RequiredContentAsync(string tenant, string principal, string reference,
        string expectedSha256, CancellationToken cancellationToken)
    {
        var bytes = await content.ReadAsync(tenant, principal, reference, cancellationToken)
            ?? throw new InvalidDataException("The protected Transaction Engine payload is unavailable.");
        var actual = Convert.ToHexString(SHA256.HashData(bytes));
        if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual),
                Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("The protected Transaction Engine payload digest does not match metadata.");
        return bytes;
    }

    private async Task<string> PreserveResponseAsync(string tenant, string principal,
        string conversationReference, ReadOnlyMemory<byte> response, CancellationToken cancellationToken)
    {
        var responseReference = await content.StoreAsync(tenant, principal,
            SubmissionContentKind.Response, response, cancellationToken);
        await conversations.RecordResponseEvidenceAsync(tenant, principal, conversationReference,
            responseReference, Convert.ToHexString(SHA256.HashData(response.Span)), cancellationToken);
        return responseReference;
    }

    private static TransactionEngineOutboundMessage Outbound(TransactionEngineOutboundKind kind,
        Uri endpoint, ReadOnlySpan<byte> content)
    {
        var bytes = content.ToArray();
        return new(kind, endpoint, ImmutableArray.Create(bytes),
            Convert.ToHexString(SHA256.HashData(bytes)));
    }
}
