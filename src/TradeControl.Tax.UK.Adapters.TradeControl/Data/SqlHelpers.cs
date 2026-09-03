using System.Data;
using Microsoft.Data.SqlClient;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public static class SqlHelpers
{
    public static string GetString(IDataRecord record, string name)
        => record[name] switch
        {
            DBNull => string.Empty,
            var value => Convert.ToString(value) ?? string.Empty
        };

    public static int GetInt32(IDataRecord record, string name)
        => record[name] == DBNull.Value ? 0 : Convert.ToInt32(record[name]);

    public static decimal GetDecimal(IDataRecord record, string name)
        => record[name] == DBNull.Value ? 0m : Convert.ToDecimal(record[name]);

    public static DateTime GetDateTime(IDataRecord record, string name)
        => record[name] == DBNull.Value ? DateTime.MinValue : Convert.ToDateTime(record[name]);

    public static async Task EnsureOpenAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }
    }
}
