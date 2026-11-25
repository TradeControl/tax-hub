using Microsoft.Data.SqlClient;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
FROM Cash.fnTaxBizBalanceSheetUK(@TaxSourceCode, @AsOfDate)
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
        }
        if (source is null)
            throw new InvalidOperationException($"Balance-sheet source '{taxSourceCode}' is not configured.");

        // @@DBTS is deliberately not used as currentness evidence here. It is database-wide,
        // so acquiring an unrelated Category Tree submission lock advances it and would make
        // the exact accounts package invalidate itself. The prepared document already depends
        // on this complete, ordered projection; use that deterministic projection as its token.
        var canonicalProjection = string.Join("\n", values
            .OrderBy(item => item.TagCode, StringComparer.Ordinal)
            .Select(item => string.Join("|",
                item.TagCode,
                item.ValueState,
                item.SupportStatus,
                item.StatutoryAmount?.ToString("0.00000", CultureInfo.InvariantCulture) ?? string.Empty)));
        var projectionToken = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",
                source,
                returnedAsOf.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                periodStart?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty,
                validation,
                canonicalProjection))));
        versions.Add(new("Cash.fnTaxBizBalanceSheetUK", projectionToken, null));

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
       LossesCarriedForward
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
        var returnedStart = SqlHelpers.GetDateTime(reader, "PeriodStart");
        var returnedEnd = SqlHelpers.GetDateTime(reader, "PeriodEnd");
        var payOn = SqlHelpers.GetDateTime(reader, "PayOn");
        var netProfit = SqlHelpers.GetDecimal(reader, "NetProfit");
        var calculatedTaxDue = SqlHelpers.GetDecimal(reader, "CalculatedTaxDue");
        var businessTaxAdjustment = SqlHelpers.GetDecimal(reader, "BusinessTaxAdjustment");
        var businessTaxRate = reader["BusinessTaxRate"] == DBNull.Value
            ? (decimal?)null
            : SqlHelpers.GetDecimal(reader, "BusinessTaxRate");
        var isUniformTaxRate = reader.GetBoolean(reader.GetOrdinal("IsUniformTaxRate"));
        var statementTaxDue = SqlHelpers.GetDecimal(reader, "StatementTaxDue");
        var statementTaxPaid = SqlHelpers.GetDecimal(reader, "StatementTaxPaid");
        var statementBalance = SqlHelpers.GetDecimal(reader, "StatementBalance");
        var previousLosses = SqlHelpers.GetDecimal(reader, "PreviousLossesCarriedForward");
        var losses = SqlHelpers.GetDecimal(reader, "LossesCarriedForward");

        // @@DBTS is database-wide and can advance because of an unrelated write between two
        // identical statutory reads. Currentness evidence must therefore describe the complete
        // projection itself, as the balance-sheet and liability readers already do.
        var canonical = string.Join("|", new object?[]
        {
            returnedStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            returnedEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            payOn.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            netProfit,
            calculatedTaxDue,
            businessTaxAdjustment,
            businessTaxRate,
            isUniformTaxRate,
            statementTaxDue,
            statementTaxPaid,
            statementBalance,
            previousLosses,
            losses
        }.Select(value => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty));
        var projectionToken = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new()
        {
            PeriodStart = returnedStart,
            PeriodEnd = returnedEnd,
            PayOn = payOn,
            NetProfit = netProfit,
            CalculatedTaxDue = calculatedTaxDue,
            BusinessTaxAdjustment = businessTaxAdjustment,
            BusinessTaxRate = businessTaxRate,
            IsUniformTaxRate = isUniformTaxRate,
            StatementTaxDue = statementTaxDue,
            StatementTaxPaid = statementTaxPaid,
            StatementBalance = statementBalance,
            PreviousLossesCarriedForward = previousLosses,
            LossesCarriedForward = losses,
            Versions = [new("Cash.fnTaxBizComputation", projectionToken, null)]
        };
    }

    public async Task<TcCorporationTaxLiabilityProjection> ReadCorporationTaxLiabilityAsync(
        string connectionString,
        DateTime periodStart,
        DateTime periodEnd,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
SELECT PeriodStart, PeriodEnd, JurisdictionCode, TaxSourceCode, ReportingProfileCode,
       TaxableResultBeforeRelief, OpeningLoss, LossCreated, LossUsed, ClosingLoss,
       TaxableTotalProfits, RelevantDistributions, AugmentedProfits, AssociatedCompanyCount,
       MainRate, SmallProfitsRate, MarginalReliefFraction, LowerLimit, UpperLimit,
       AdjustedLowerLimit, AdjustedUpperLimit, TaxBeforeMarginalRelief, MarginalRelief,
       CorporationTaxChargeable, BusinessTaxAdjustment, TaxDue, CalculationCode,
       RateFindingCode, ProfileFindingCode, KernelFindingCode, IsAccrualReady, IsFilingReady
FROM Cash.vwTaxCorpLiabilityUK
WHERE PeriodStart = @PeriodStart
  AND PeriodEnd = @PeriodEnd;
""";
        using var connection = _connectionFactory.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@PeriodStart", periodStart.Date);
        command.Parameters.AddWithValue("@PeriodEnd", periodEnd.Date);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException(
                $"No reviewed UK Corporation Tax liability exists for {periodStart:yyyy-MM-dd} to {periodEnd:yyyy-MM-dd} (inclusive).");

        string? OptionalString(string name) => reader[name] == DBNull.Value
            ? null
            : SqlHelpers.GetString(reader, name);
        var projection = new TcCorporationTaxLiabilityProjection
        {
            PeriodStart = SqlHelpers.GetDateTime(reader, "PeriodStart"),
            PeriodEnd = SqlHelpers.GetDateTime(reader, "PeriodEnd"),
            JurisdictionCode = SqlHelpers.GetString(reader, "JurisdictionCode"),
            TaxSourceCode = SqlHelpers.GetString(reader, "TaxSourceCode"),
            ReportingProfileCode = SqlHelpers.GetString(reader, "ReportingProfileCode"),
            TaxableResultBeforeRelief = SqlHelpers.GetDecimal(reader, "TaxableResultBeforeRelief"),
            OpeningLoss = SqlHelpers.GetDecimal(reader, "OpeningLoss"),
            LossCreated = SqlHelpers.GetDecimal(reader, "LossCreated"),
            LossUsed = SqlHelpers.GetDecimal(reader, "LossUsed"),
            ClosingLoss = SqlHelpers.GetDecimal(reader, "ClosingLoss"),
            TaxableTotalProfits = SqlHelpers.GetDecimal(reader, "TaxableTotalProfits"),
            RelevantDistributions = SqlHelpers.GetDecimal(reader, "RelevantDistributions"),
            AugmentedProfits = SqlHelpers.GetDecimal(reader, "AugmentedProfits"),
            AssociatedCompanyCount = reader.GetInt32(reader.GetOrdinal("AssociatedCompanyCount")),
            MainRate = SqlHelpers.GetDecimal(reader, "MainRate"),
            SmallProfitsRate = SqlHelpers.GetDecimal(reader, "SmallProfitsRate"),
            MarginalReliefFraction = SqlHelpers.GetDecimal(reader, "MarginalReliefFraction"),
            LowerLimit = SqlHelpers.GetDecimal(reader, "LowerLimit"),
            UpperLimit = SqlHelpers.GetDecimal(reader, "UpperLimit"),
            AdjustedLowerLimit = SqlHelpers.GetDecimal(reader, "AdjustedLowerLimit"),
            AdjustedUpperLimit = SqlHelpers.GetDecimal(reader, "AdjustedUpperLimit"),
            TaxBeforeMarginalRelief = SqlHelpers.GetDecimal(reader, "TaxBeforeMarginalRelief"),
            MarginalRelief = SqlHelpers.GetDecimal(reader, "MarginalRelief"),
            CorporationTaxChargeable = SqlHelpers.GetDecimal(reader, "CorporationTaxChargeable"),
            BusinessTaxAdjustment = SqlHelpers.GetDecimal(reader, "BusinessTaxAdjustment"),
            TaxDue = SqlHelpers.GetDecimal(reader, "TaxDue"),
            CalculationCode = SqlHelpers.GetString(reader, "CalculationCode"),
            RateFindingCode = OptionalString("RateFindingCode"),
            ProfileFindingCode = OptionalString("ProfileFindingCode"),
            KernelFindingCode = OptionalString("KernelFindingCode"),
            IsAccrualReady = reader.GetBoolean(reader.GetOrdinal("IsAccrualReady")),
            IsFilingReady = reader.GetBoolean(reader.GetOrdinal("IsFilingReady"))
        };

        var canonical = string.Join("|", new object?[]
        {
            projection.PeriodStart.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            projection.PeriodEnd.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            projection.JurisdictionCode, projection.TaxSourceCode, projection.ReportingProfileCode,
            projection.TaxableResultBeforeRelief, projection.OpeningLoss, projection.LossCreated,
            projection.LossUsed, projection.ClosingLoss, projection.TaxableTotalProfits,
            projection.RelevantDistributions, projection.AugmentedProfits, projection.AssociatedCompanyCount,
            projection.MainRate, projection.SmallProfitsRate, projection.MarginalReliefFraction,
            projection.LowerLimit, projection.UpperLimit, projection.AdjustedLowerLimit,
            projection.AdjustedUpperLimit, projection.TaxBeforeMarginalRelief, projection.MarginalRelief,
            projection.CorporationTaxChargeable, projection.BusinessTaxAdjustment, projection.TaxDue,
            projection.CalculationCode, projection.RateFindingCode, projection.ProfileFindingCode,
            projection.KernelFindingCode, projection.IsAccrualReady, projection.IsFilingReady
        }.Select(value => value is IFormattable formattable
            ? formattable.ToString(null, CultureInfo.InvariantCulture)
            : value?.ToString() ?? string.Empty));
        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return projection with { Versions = [new("Cash.vwTaxCorpLiabilityUK", token, null)] };
    }
}
