using System.Data;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TradeControlTaxSourceAdapter : IVatReturnSourceReader, IBusinessIncomeSourceReader, ISourceReadinessEvaluator
{
    private readonly ConnectionFactory _connections;
    private readonly ISourceConnectionResolver _sources;

    public TradeControlTaxSourceAdapter(ConnectionFactory connections, ISourceConnectionResolver sources)
    {
        _connections = connections;
        _sources = sources;
    }

    public async Task<VatReturnSource> ReadAsync(VatReturnSelector selector, CancellationToken cancellationToken = default)
    {
        var connectionString = _sources.Resolve(selector.Source);
        var versionBefore = await ReadDatabaseVersionAsync(connectionString, cancellationToken);
        var context = await new TcStatutoryContextReader(_connections, connectionString)
            .ReadAsync(selector.Period.End, cancellationToken);
        const string sql = """
SELECT CONVERT(date, due.PayFrom) AS PeriodStart,
       CONVERT(date, DATEADD(day, -1, due.PayTo)) AS PeriodEnd,
       submission.vatDueSales, submission.vatDueAcquisitions, submission.vatAdjustment,
       submission.totalVatDue, submission.vatReclaimedCurrPeriod, submission.netVatDue,
       submission.totalValueSalesExVAT, submission.totalValuePurchasesExVAT,
       submission.totalValueGoodsSuppliedExVAT, submission.totalValueGoodsReceivedExVAT,
       CONVERT(varchar(18), @@DBTS, 1) AS SnapshotToken
FROM Cash.vwTaxVatSubmission submission
JOIN Cash.fnTaxTypeDueDates(1, 0) due
  ON submission.StartOn = due.PayTo
WHERE CONVERT(date, submission.VatEndOn) = @VatEndOn;
""";
        using var connection = _connections.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@VatEndOn", SqlDbType.Date).Value = selector.Period.End.ToDateTime(TimeOnly.MinValue);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("No VAT dataset row was found for the requested VAT end date.");
        var row = new TcVatProjectionRow(Date(reader, "PeriodStart"), Date(reader, "PeriodEnd"),
            Decimal(reader, "vatDueSales"), Decimal(reader, "vatDueAcquisitions"), Decimal(reader, "vatAdjustment"),
            Decimal(reader, "totalVatDue"),
            Decimal(reader, "vatReclaimedCurrPeriod"), Decimal(reader, "netVatDue"), Decimal(reader, "totalValueSalesExVAT"),
            Decimal(reader, "totalValuePurchasesExVAT"), Decimal(reader, "totalValueGoodsSuppliedExVAT"),
            Decimal(reader, "totalValueGoodsReceivedExVAT"));
        var token = Convert.ToString(reader["SnapshotToken"]) ?? throw new InvalidOperationException("VAT snapshot token is missing.");
        if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("The VAT period returned duplicate rows.");
        if (!versionBefore.Equals(token, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Trade Control source changed while the VAT snapshot was being read.");
        var resolvedPeriod = new TaxReportingPeriod(row.PeriodStart, row.PeriodEnd,
            TaxPeriodKind.Vat, selector.Period.StableKey);
        return TradeControlSourceMapper.Vat(TradeControlSourceMapper.Subject(context), resolvedPeriod, row, token);
    }

    public async Task<BusinessIncomeSource> ReadAsync(BusinessIncomeSelector selector, CancellationToken cancellationToken = default)
    {
        var connectionString = _sources.Resolve(selector.Source);
        var versionBefore = await ReadDatabaseVersionAsync(connectionString, cancellationToken);
        var context = await new TcStatutoryContextReader(_connections, connectionString)
            .ReadAsync(selector.Period.End, cancellationToken);
        const string sql = """
SELECT projection.TaxSourceCode, projection.PeriodStart, projection.PeriodEnd,
       projection.ValidationStatus, projection.TagCode, tag.TagName,
       projection.CashPolarityCode, projection.SupportStatus, projection.StatutoryAmount
FROM Cash.fnTaxBizCumulative(@TaxSourceCode, @PeriodStart, @PeriodEnd) projection
JOIN Cash.tbTaxTag tag ON tag.TaxSourceCode = projection.TaxSourceCode AND tag.TagCode = projection.TagCode
ORDER BY projection.TagCode;
SELECT TagCode, CashCode, PeriodStartOn, CashCodeRowVer, PeriodRowVer
FROM Cash.fnTaxBizCumulativeContributors(@TaxSourceCode, @PeriodStart, @PeriodEnd)
ORDER BY TagCode, CashCode, PeriodStartOn;
""";
        using var connection = _connections.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@TaxSourceCode", SqlDbType.NVarChar, 20).Value = selector.TaxSourceCode.Value;
        command.Parameters.Add("@PeriodStart", SqlDbType.Date).Value = selector.Period.Start.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@PeriodEnd", SqlDbType.Date).Value = selector.Period.End.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var rows = new List<TcBusinessProjectionRow>();
        var contributors = new List<TcContributorRow>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(new(String(reader, "TaxSourceCode"), Date(reader, "PeriodStart"), Date(reader, "PeriodEnd"),
                String(reader, "ValidationStatus"), String(reader, "TagCode"), String(reader, "TagName"),
                Convert.ToInt16(reader["CashPolarityCode"]), String(reader, "SupportStatus"),
                reader["StatutoryAmount"] == DBNull.Value ? null : Decimal(reader, "StatutoryAmount")));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            contributors.Add(new(String(reader, "TagCode"), String(reader, "CashCode"), Date(reader, "PeriodStartOn"),
                Convert.ToHexString((byte[])reader["CashCodeRowVer"]), Convert.ToHexString((byte[])reader["PeriodRowVer"])));
        await reader.CloseAsync();
        var versionAfter = await ReadDatabaseVersionAsync(connectionString, cancellationToken);
        if (!versionBefore.Equals(versionAfter, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The Trade Control source changed while the cumulative snapshot was being read.");
        return TradeControlSourceMapper.Business(TradeControlSourceMapper.Subject(context), selector.BusinessId,
            selector.TaxSourceCode, selector.Period, rows, contributors);
    }

    public async Task<SourceReadiness> EvaluateAsync(SourceReadinessRequest request, CancellationToken cancellationToken = default)
    {
        var connectionString = _sources.Resolve(request.Source);
        const string sql = """
SELECT FindingCode, SeverityCode, ScopeCode, RecordCode, FindingMessage
FROM App.fnStatutoryContextReadiness(@SubjectCode, @ReportingTypeCode, @TaxSourceCode, NULL, NULL, @AsOfDate);
SELECT N'TAX-TAG-MAPPING-INVALID' AS FindingCode, N'ERROR' AS SeverityCode,
       N'MAPPING' AS ScopeCode, COALESCE(TagCode, CashCode, CategoryCode) AS RecordCode,
       Message AS FindingMessage
FROM Cash.fnTaxTagMapValidate(@TaxSourceCode)
WHERE @TaxSourceCode IS NOT NULL AND IsError = 1;
""";
        using var connection = _connections.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@SubjectCode", SqlDbType.NVarChar, 50).Value = request.Subject.SubjectCode;
        command.Parameters.Add("@ReportingTypeCode", SqlDbType.NVarChar, 20).Value =
            request.Period.Kind == TaxPeriodKind.Vat ? "INDIRECT-TAX" : "SELF-EMPLOYMENT";
        command.Parameters.Add("@TaxSourceCode", SqlDbType.NVarChar, 20).Value =
            request.TaxSourceCode is { } source ? source.Value : DBNull.Value;
        command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = request.Period.End.ToDateTime(TimeOnly.MinValue);
        var findings = new List<SourceFinding>();
        using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await ReadFindingsAsync(ReadinessScope.Source);
        await reader.NextResultAsync(cancellationToken);
        await ReadFindingsAsync(ReadinessScope.Mapping);
        return new(findings);

        async Task ReadFindingsAsync(ReadinessScope fallbackScope)
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var severity = String(reader, "SeverityCode").Equals("ERROR", StringComparison.OrdinalIgnoreCase)
                    ? SourceFindingSeverity.Error : SourceFindingSeverity.Warning;
                findings.Add(new(fallbackScope, severity, String(reader, "FindingCode"),
                    String(reader, "FindingMessage"), reader["RecordCode"] == DBNull.Value ? null : String(reader, "RecordCode")));
            }
        }
    }

    private static string String(IDataRecord row, string name) => Convert.ToString(row[name]) ?? string.Empty;
    private static decimal Decimal(IDataRecord row, string name) => Convert.ToDecimal(row[name]);
    private static DateOnly Date(IDataRecord row, string name) => DateOnly.FromDateTime(Convert.ToDateTime(row[name]));

    private async Task<string> ReadDatabaseVersionAsync(string connectionString, CancellationToken cancellationToken)
    {
        using var connection = _connections.Create(connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand("SELECT CONVERT(varchar(18), @@DBTS, 1);", connection);
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken))
            ?? throw new InvalidOperationException("The Trade Control source version could not be read.");
    }
}
