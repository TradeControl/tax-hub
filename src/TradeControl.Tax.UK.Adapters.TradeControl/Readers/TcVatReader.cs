using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TcVatReader
{
    private readonly ConnectionFactory _connectionFactory;

    public TcVatReader(ConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<TcVatStatement?> ReadAsync(
        string connectionString,
        DateTime periodEndOn,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
SELECT YearNumber,
       Description,
       Period,
       StartOn,
       VatEndOn,
       vatDueSales,
       vatDueAcquisitions,
       totalVatDue,
       vatReclaimedCurrPeriod,
       netVatDue,
       totalValueSalesExVAT,
       totalValuePurchasesExVAT,
       totalValueGoodsSuppliedExVAT,
       totalValueGoodsReceivedExVAT
FROM Cash.vwTaxVatSubmission
WHERE StartOn = @StartOn;
""";

        using var connection = _connectionFactory.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@StartOn", periodEndOn);

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new TcVatStatement
        {
            YearNumber = SqlHelpers.GetInt32(reader, "YearNumber"),
            Description = SqlHelpers.GetString(reader, "Description"),
            Period = SqlHelpers.GetString(reader, "Period"),
            StartOn = SqlHelpers.GetDateTime(reader, "StartOn"),
            VatEndOn = SqlHelpers.GetDateTime(reader, "VatEndOn"),
            VatDueSales = SqlHelpers.GetDecimal(reader, "vatDueSales"),
            VatDueAcquisitions = SqlHelpers.GetDecimal(reader, "vatDueAcquisitions"),
            TotalVatDue = SqlHelpers.GetDecimal(reader, "totalVatDue"),
            VatReclaimedCurrPeriod = Math.Abs(SqlHelpers.GetDecimal(reader, "vatReclaimedCurrPeriod")),
            NetVatDue = SqlHelpers.GetDecimal(reader, "netVatDue"),
            TotalValueSalesExVat = SqlHelpers.GetDecimal(reader, "totalValueSalesExVAT"),
            TotalValuePurchasesExVat = SqlHelpers.GetDecimal(reader, "totalValuePurchasesExVAT"),
            TotalValueGoodsSuppliedExVat = SqlHelpers.GetDecimal(reader, "totalValueGoodsSuppliedExVAT"),
            TotalValueGoodsReceivedExVat = SqlHelpers.GetDecimal(reader, "totalValueGoodsReceivedExVAT")
        };
    }
}
