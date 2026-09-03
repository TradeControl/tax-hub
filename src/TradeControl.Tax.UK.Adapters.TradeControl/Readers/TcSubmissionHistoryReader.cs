using TradeControl.Tax.UK.Adapters.TradeControl.Data;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TcSubmissionHistoryReader
{
    public Task<IReadOnlyList<TcSubmissionHistory>> ReadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TcSubmissionHistory> rows = Array.Empty<TcSubmissionHistory>();
        return Task.FromResult(rows);
    }
}
