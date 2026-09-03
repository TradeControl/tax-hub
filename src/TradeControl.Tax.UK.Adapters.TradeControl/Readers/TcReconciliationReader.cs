using TradeControl.Tax.UK.Adapters.TradeControl.Data;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TcReconciliationReader
{
    public Task<IReadOnlyList<TcReconciliation>> ReadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TcReconciliation> rows = Array.Empty<TcReconciliation>();
        return Task.FromResult(rows);
    }
}
