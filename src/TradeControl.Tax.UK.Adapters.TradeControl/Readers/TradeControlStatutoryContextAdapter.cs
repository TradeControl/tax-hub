using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TradeControlStatutoryContextAdapter : ISourceStatutoryContextReader
{
    private readonly ConnectionFactory _connections;
    private readonly ISourceConnectionResolver _sources;

    public TradeControlStatutoryContextAdapter(ConnectionFactory connections, ISourceConnectionResolver sources)
    {
        _connections = connections;
        _sources = sources;
    }

    public Task<StatutoryContextSnapshot> ReadAsync(
        SourceKey source, DateOnly asOfDate, CancellationToken cancellationToken = default) =>
        new Readers.TcStatutoryContextReader(_connections, _sources.Resolve(source))
            .ReadAsync(asOfDate, cancellationToken);
}
