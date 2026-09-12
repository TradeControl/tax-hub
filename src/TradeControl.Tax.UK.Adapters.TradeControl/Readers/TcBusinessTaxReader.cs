using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

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
       CashPolarityCode, SupportStatus, StatutoryAmount
FROM Cash.fnTaxBizCumulative(@TaxSourceCode, @PeriodStart, @PeriodEnd)
ORDER BY TagCode;
SELECT TagCode, CashCode, PeriodStartOn, CashCodeRowVer, PeriodRowVer, CashCodeUpdatedOn
FROM Cash.fnTaxBizCumulativeContributors(@TaxSourceCode, @PeriodStart, @PeriodEnd)
ORDER BY TagCode, CashCode, PeriodStartOn;
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
                Orientation = reader.GetInt16(reader.GetOrdinal("CashPolarityCode")) == 1
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

        var versions = new List<SourceVersion>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cashVersion = Convert.ToHexString((byte[])reader["CashCodeRowVer"]);
            var periodVersion = Convert.ToHexString((byte[])reader["PeriodRowVer"]);
            versions.Add(new(
                $"Cash.fnTaxBizCumulativeContributors:{SqlHelpers.GetString(reader, "TagCode")}:{SqlHelpers.GetString(reader, "CashCode")}:{SqlHelpers.GetDateTime(reader, "PeriodStartOn"):yyyy-MM-dd}",
                $"{cashVersion}:{periodVersion}",
                reader["CashCodeUpdatedOn"] == DBNull.Value ? null : Convert.ToDateTime(reader["CashCodeUpdatedOn"])));
        }

        return new TcCumulativeProjection
        {
            TaxSourceCode = returnedSource,
            PeriodStart = returnedStart,
            PeriodEnd = returnedEnd,
            ValidationStatus = validationStatus,
            Values = values,
            Versions = versions
        };
    }

    public async Task<TcBalanceSheetProjection> ReadBalanceSheetAsync(
        string connectionString,
        string taxSourceCode,
        DateTime asOfDate,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
SELECT TaxSourceCode, AsOfDate, PeriodStart, ValidationStatus, TagCode,
       ValueState, SupportStatus, StatutoryAmount, SnapshotRowVer
FROM Cash.fnTaxBizBalanceSheet(@TaxSourceCode, @AsOfDate)
ORDER BY TagCode;
""";
        using var connection = _connectionFactory.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@TaxSourceCode", taxSourceCode);
        command.Parameters.AddWithValue("@AsOfDate", asOfDate.Date);
        var values = new List<TcBalanceSheetProjectionValue>();
        var versions = new List<SourceVersion>();
        string? source = null;
        DateTime returnedAsOf = default;
        DateTime? periodStart = null;
        var validation = TcTaxValidationStatus.Invalid;
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            source ??= SqlHelpers.GetString(reader, "TaxSourceCode");
            returnedAsOf = SqlHelpers.GetDateTime(reader, "AsOfDate");
            periodStart = reader["PeriodStart"] == DBNull.Value ? null : Convert.ToDateTime(reader["PeriodStart"]);
            validation = Enum.Parse<TcTaxValidationStatus>(SqlHelpers.GetString(reader, "ValidationStatus"), true);
            var support = Enum.Parse<TcTaxSupportStatus>(SqlHelpers.GetString(reader, "SupportStatus"), true);
            values.Add(new()
            {
                TagCode = SqlHelpers.GetString(reader, "TagCode"),
                ValueState = SqlHelpers.GetString(reader, "ValueState"),
                SupportStatus = support,
                StatutoryAmount = support == TcTaxSupportStatus.Supported ? SqlHelpers.GetDecimal(reader, "StatutoryAmount") : null
            });
            var rowVersion = Convert.ToHexString((byte[])reader["SnapshotRowVer"]);
            if (!versions.Any(item => item.RowVersion == rowVersion))
                versions.Add(new("Cash.fnTaxBizBalanceSheet", rowVersion, null));
        }
        if (source is null)
            throw new InvalidOperationException($"Balance-sheet source '{taxSourceCode}' is not configured.");
        return new()
        {
            TaxSourceCode = source,
            AsOfDate = returnedAsOf,
            PeriodStart = periodStart,
            ValidationStatus = validation,
            Values = values,
            Versions = versions
        };
    }

    public async Task<TcCorporationTaxProjection> ReadCorporationTaxAsync(
        string connectionString,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
SELECT PeriodStart, PeriodEnd, PayOn, NetProfit, CalculatedTaxDue,
       BusinessTaxAdjustment, BusinessTaxRate, IsUniformTaxRate,
       StatementTaxDue, StatementTaxPaid, StatementBalance, PreviousLossesCarriedForward,
       LossesCarriedForward, SnapshotRowVer
FROM Cash.fnTaxBizComputation(@PeriodStart, @PeriodEnd);
""";
        using var connection = _connectionFactory.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@PeriodStart", periodStart.Date);
        command.Parameters.AddWithValue("@PeriodEnd", periodEnd.Date);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                $"No Corporation Tax due-date window exists for {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd} (exclusive).");
        var rowVersion = Convert.ToHexString((byte[])reader["SnapshotRowVer"]);
        return new()
        {
            PeriodStart = SqlHelpers.GetDateTime(reader, "PeriodStart"),
            PeriodEnd = SqlHelpers.GetDateTime(reader, "PeriodEnd"),
            PayOn = SqlHelpers.GetDateTime(reader, "PayOn"),
            NetProfit = SqlHelpers.GetDecimal(reader, "NetProfit"),
            CalculatedTaxDue = SqlHelpers.GetDecimal(reader, "CalculatedTaxDue"),
            BusinessTaxAdjustment = SqlHelpers.GetDecimal(reader, "BusinessTaxAdjustment"),
            BusinessTaxRate = reader["BusinessTaxRate"] == DBNull.Value ? null : SqlHelpers.GetDecimal(reader, "BusinessTaxRate"),
            IsUniformTaxRate = reader.GetBoolean(reader.GetOrdinal("IsUniformTaxRate")),
            StatementTaxDue = SqlHelpers.GetDecimal(reader, "StatementTaxDue"),
            StatementTaxPaid = SqlHelpers.GetDecimal(reader, "StatementTaxPaid"),
            StatementBalance = SqlHelpers.GetDecimal(reader, "StatementBalance"),
            PreviousLossesCarriedForward = SqlHelpers.GetDecimal(reader, "PreviousLossesCarriedForward"),
            LossesCarriedForward = SqlHelpers.GetDecimal(reader, "LossesCarriedForward"),
            Versions = [new("Cash.fnTaxBizComputation", rowVersion, null)]
        };
    }
}
