using Microsoft.Data.SqlClient;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class ConnectionFactory
{
    public SqlConnection Create(string connectionString)
    {
        return new SqlConnection(connectionString);
    }
}
