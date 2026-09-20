using System.Data;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.Data;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Data;

public sealed class TradeControlSelfEmploymentFilingContextAdapter : ISelfEmploymentFilingContextReader
{
    private readonly ConnectionFactory _connections;
    private readonly ISourceConnectionResolver _sources;

    public TradeControlSelfEmploymentFilingContextAdapter(
        ConnectionFactory connections, ISourceConnectionResolver sources)
    {
        _connections = connections;
        _sources = sources;
    }

    public async Task<SelfEmploymentFilingContext> ReadAsync(
        SelfEmploymentFilingContextSelector selector,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
SELECT RegistrationValue, CONVERT(varchar(18), RowVer, 1) AS RowVersion, IsReviewed
FROM Subject.fnRegistration(@SubjectCode, N'GB-NI', @AsOfDate);

SELECT ReportingProfileCode, AuthorityReference, CONVERT(varchar(18), RowVer, 1) AS RowVersion, IsReviewed
FROM Cash.fnReportingProfile(@SubjectCode, N'SELF-EMPLOYMENT', @TaxSourceCode, @AsOfDate);

SELECT setting.SettingCode, setting.DisplayValue, setting.RowVersion, setting.IsReviewed
FROM Cash.fnReportingProfile(@SubjectCode, N'SELF-EMPLOYMENT', @TaxSourceCode, @AsOfDate) profile
CROSS APPLY
(
    SELECT N'ACCOUNTING-BASIS' AS SettingCode, TextValue AS DisplayValue,
           CONVERT(varchar(18), RowVer, 1) AS RowVersion, IsReviewed
    FROM Cash.fnReportingProfileSetting(profile.SubjectCode, profile.ReportingProfileCode,
        N'ACCOUNTING-BASIS', @AsOfDate)
    UNION ALL
    SELECT N'QUARTERLY-PERIOD-TYPE', TextValue, CONVERT(varchar(18), RowVer, 1), IsReviewed
    FROM Cash.fnReportingProfileSetting(profile.SubjectCode, profile.ReportingProfileCode,
        N'QUARTERLY-PERIOD-TYPE', @AsOfDate)
) setting;
""";
        using var connection = _connections.Create(_sources.Resolve(selector.Source));
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@SubjectCode", SqlDbType.NVarChar, 50).Value = selector.SubjectCode;
        command.Parameters.Add("@TaxSourceCode", SqlDbType.NVarChar, 20).Value = selector.TaxSourceCode.Value;
        command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = selector.AsOfDate.ToDateTime(TimeOnly.MinValue);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var registrations = await RowsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var profiles = await RowsAsync(reader, cancellationToken);
        await reader.NextResultAsync(cancellationToken);
        var settings = await RowsAsync(reader, cancellationToken);

        if (registrations.Count != 1) throw new InvalidOperationException(
            "Exactly one effective National Insurance registration is required.");
        if (profiles.Count != 1) throw new InvalidOperationException(
            "Exactly one effective self-employment reporting profile is required.");
        if (!bool.TryParse(registrations[0][2], out var registrationReviewed) || !registrationReviewed)
            throw new InvalidOperationException("The effective National Insurance registration has not been reviewed.");
        if (!bool.TryParse(profiles[0][3], out var profileReviewed) || !profileReviewed)
            throw new InvalidOperationException("The effective self-employment reporting profile has not been reviewed.");
        var settingValues = settings.ToDictionary(row => row[0], row => row, StringComparer.OrdinalIgnoreCase);
        if (!settingValues.TryGetValue("ACCOUNTING-BASIS", out var accounting))
            throw new InvalidOperationException("The effective accounting-basis setting is missing.");
        if (!settingValues.TryGetValue("QUARTERLY-PERIOD-TYPE", out var periodType))
            throw new InvalidOperationException("The effective quarterly-period-type setting is missing.");
        if (new[] { accounting, periodType }.Any(row =>
            !bool.TryParse(row[3], out var reviewed) || !reviewed))
            throw new InvalidOperationException("An effective self-employment reporting setting has not been reviewed.");

        var provenance = new List<FactProvenance>
        {
            new("TradeControl", "Subject.fnRegistration", "GB-NI", registrations[0][1]),
            new("TradeControl", "Cash.fnReportingProfile", profiles[0][0], profiles[0][2]),
            new("TradeControl", "Cash.fnReportingProfileSetting", "ACCOUNTING-BASIS", accounting[2]),
            new("TradeControl", "Cash.fnReportingProfileSetting", "QUARTERLY-PERIOD-TYPE", periodType[2])
        };
        return new(registrations[0][0], new(profiles[0][1]), accounting[1], periodType[1], provenance);
    }

    private static async Task<List<string[]>> RowsAsync(SqlDataReader reader, CancellationToken cancellationToken)
    {
        var rows = new List<string[]>();
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => Convert.ToString(reader[index]) ?? string.Empty).ToArray());
        return rows;
    }
}
