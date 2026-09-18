using TradeControl.Tax.Data;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public interface ISourceConnectionResolver
{
    string Resolve(SourceKey source);
}

public sealed class SourceConnectionResolver : ISourceConnectionResolver
{
    private readonly IReadOnlyDictionary<string, string> _connections;

    public SourceConnectionResolver(IEnumerable<KeyValuePair<SourceKey, string>> connections)
    {
        _connections = connections.ToDictionary(
            item => item.Key.Value,
            item => string.IsNullOrWhiteSpace(item.Value)
                ? throw new ArgumentException($"Source '{item.Key}' has no connection string.", nameof(connections))
                : item.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public string Resolve(SourceKey source) => _connections.TryGetValue(source.Value, out var connection)
        ? connection
        : throw new KeyNotFoundException($"Source '{source}' is not configured.");
}
