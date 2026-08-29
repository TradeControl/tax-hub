using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.Infrastructure.Db;
using TradeControl.Tax.UK.Models.Tc;

namespace TradeControl.Tax.UK.Services.TcData;

public sealed class TcBusinessTaxReader
{
    private readonly ConnectionFactory _connectionFactory;

    public TcBusinessTaxReader(ConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task<IReadOnlyList<TcBusinessTaxView>> ReadAsync(
        string connectionString,
        string taxSourceCode,
        DateTime periodTo,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
SELECT TaxSourceCode,
       TagCode,
       PeriodFrom,
       PeriodTo,
       TaxableAmount
FROM Cash.vwTaxBizSubmission
WHERE TaxSourceCode = @TaxSourceCode
  AND PeriodTo = @PeriodTo
ORDER BY TagCode;
""";

        using var connection = _connectionFactory.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@TaxSourceCode", taxSourceCode);
        command.Parameters.AddWithValue("@PeriodTo", periodTo);

        var rows = new List<TcBusinessTaxView>();

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new TcBusinessTaxView
            {
                TaxSourceCode = SqlHelpers.GetString(reader, "TaxSourceCode"),
                TagCode = SqlHelpers.GetString(reader, "TagCode"),
                PeriodFrom = SqlHelpers.GetDateTime(reader, "PeriodFrom"),
                PeriodTo = SqlHelpers.GetDateTime(reader, "PeriodTo"),
                // Legacy MICRO projection semantics; cumulative statutory projection uses ReadCumulativeAsync.
                TaxableAmount = Math.Abs(SqlHelpers.GetDecimal(reader, "TaxableAmount"))
            });
        }

        return rows;
    }

    public async Task<TcCumulativeProjection> ReadCumulativeAsync(
        string connectionString,
        string taxSourceCode,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
SELECT TaxSourceCode, PeriodStart, PeriodEnd, ValidationStatus, TagCode,
       StatutoryPolarityCode, SupportStatus, StatutoryAmount
FROM Cash.fnTaxBizCumulative(@TaxSourceCode, @PeriodStart, @PeriodEnd)
ORDER BY TagCode;
""";

        using var connection = _connectionFactory.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);

        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@TaxSourceCode", taxSourceCode);
        command.Parameters.AddWithValue("@PeriodStart", periodStart.Date);
        command.Parameters.AddWithValue("@PeriodEnd", periodEnd.Date);

        var values = new List<TcCumulativeProjectionValue>();
        string? returnedSource = null;
        DateTime returnedStart = default;
        DateTime returnedEnd = default;
        var validationStatus = TcTaxValidationStatus.Invalid;

        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            returnedSource ??= SqlHelpers.GetString(reader, "TaxSourceCode");
            returnedStart = SqlHelpers.GetDateTime(reader, "PeriodStart");
            returnedEnd = SqlHelpers.GetDateTime(reader, "PeriodEnd");
            validationStatus = Enum.Parse<TcTaxValidationStatus>(
                SqlHelpers.GetString(reader, "ValidationStatus"), true);
            var supportStatus = Enum.Parse<TcTaxSupportStatus>(
                SqlHelpers.GetString(reader, "SupportStatus"), true);

            values.Add(new TcCumulativeProjectionValue
            {
                TagCode = SqlHelpers.GetString(reader, "TagCode"),
                Orientation = reader.GetInt16(reader.GetOrdinal("StatutoryPolarityCode")) == 1
                    ? TcTaxOrientation.Income
                    : TcTaxOrientation.Expense,
                SupportStatus = supportStatus,
                StatutoryAmount = supportStatus == TcTaxSupportStatus.Supported
                    ? SqlHelpers.GetDecimal(reader, "StatutoryAmount")
                    : null
            });
        }

        if (returnedSource is null)
            throw new InvalidOperationException($"Tax source '{taxSourceCode}' is not configured.");

        return new TcCumulativeProjection
        {
            TaxSourceCode = returnedSource,
            PeriodStart = returnedStart,
            PeriodEnd = returnedEnd,
            ValidationStatus = validationStatus,
            Values = values
        };
    }
}
