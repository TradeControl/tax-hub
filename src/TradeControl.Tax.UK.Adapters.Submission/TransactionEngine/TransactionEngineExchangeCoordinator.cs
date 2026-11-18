namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

/// <summary>
/// Connects one durable protocol transition to one bounded HTTP exchange.
/// It never retries: uncertain submission recovery remains an explicit
/// DATA_REQUEST operation selected by the caller after inspecting durable state.
/// </summary>
internal sealed class TransactionEngineExchangeCoordinator(
    TransactionEngineConversationCoordinator conversations,
    TransactionEngineHttpGateway gateway)
{
    internal async Task<TransactionEngineConversationRecord> SubmitOnceAsync(
        string tenant, string principal, string conversationReference,
        CancellationToken cancellationToken = default)
    {
        var outbound = await conversations.BeginSubmissionAsync(
            tenant, principal, conversationReference, cancellationToken);
        var exchange = await gateway.ExchangeOnceAsync(outbound, cancellationToken);
        return await conversations.ApplySubmissionResponseAsync(
            tenant, principal, conversationReference, exchange.Content.ToArray(), cancellationToken);
    }

    internal async Task<TransactionEngineConversationRecord> RecoverOnceAsync(
        string tenant, string principal, string conversationReference,
        TransactionEngineSubmissionIdentity identity,
        CancellationToken cancellationToken = default)
    {
        var outbound = await conversations.BuildDataRequestAsync(
            tenant, principal, conversationReference, identity, cancellationToken);
        var exchange = await gateway.ExchangeOnceAsync(outbound, cancellationToken);
        return await conversations.ApplyDataResponseAsync(
            tenant, principal, conversationReference, exchange.Content.ToArray(), cancellationToken);
    }

    internal async Task<TransactionEngineConversationRecord> PollOnceAsync(
        string tenant, string principal, string conversationReference,
        CancellationToken cancellationToken = default)
    {
        var outbound = await conversations.BuildPollAsync(
            tenant, principal, conversationReference, cancellationToken);
        var exchange = await gateway.ExchangeOnceAsync(outbound, cancellationToken);
        return await conversations.ApplySubmissionResponseAsync(
            tenant, principal, conversationReference, exchange.Content.ToArray(), cancellationToken);
    }

    internal async Task<TransactionEngineConversationRecord> DeleteOnceAsync(
        string tenant, string principal, string conversationReference,
        CancellationToken cancellationToken = default)
    {
        var outbound = await conversations.BeginOrResumeDeleteAsync(
            tenant, principal, conversationReference, cancellationToken);
        var exchange = await gateway.ExchangeOnceAsync(outbound, cancellationToken);
        return await conversations.ApplyDeleteResponseAsync(
            tenant, principal, conversationReference, exchange.Content.ToArray(), cancellationToken);
    }
}
