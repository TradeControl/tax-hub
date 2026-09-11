using System.Data;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Application.DataProvision;

namespace TradeControl.Tax.UK.Adapters.TradeControl.Readers;

public sealed class TcStatutoryContextReader : IStatutoryContextSource
{
    private readonly ConnectionFactory _connectionFactory;
    private readonly string _connectionString;

    public TcStatutoryContextReader(ConnectionFactory connectionFactory, string connectionString)
    {
        _connectionFactory = connectionFactory;
        _connectionString = connectionString;
    }

    public async Task<StatutoryContextSnapshot> ReadAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken = default)
    {
        using var connection = _connectionFactory.Create(_connectionString);
        await SqlHelpers.EnsureOpenAsync(connection, cancellationToken);
        using var command = new SqlCommand("App.proc_StatutoryContext", connection)
        {
            CommandType = CommandType.StoredProcedure
        };
        command.Parameters.Add("@AsOfDate", SqlDbType.Date).Value = asOfDate.ToDateTime(TimeOnly.MinValue);
        using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The home statutory identity could not be resolved.");

        var versions = new List<SourceVersion>();
        AddVersion("App.tbOptions", "OptionsRowVer", null);
        AddVersion("Subject.tbSubject", "SubjectRowVer", "SubjectUpdatedOn");
        AddVersion("Subject.tbAddress:Trading", "TradingAddressRowVer", "TradingAddressUpdatedOn");
        AddVersion("Subject.tbAddress:Registered", "RegisteredAddressRowVer", "RegisteredAddressUpdatedOn");
        AddVersion("Subject.tbVirtual", "VirtualRowVer", null);

        var identity = new StatutoryIdentityEvidence(
            GetRequired("SubjectCode"), GetRequired("SubjectName"), Convert.ToInt16(reader["BusinessTaxTypeCode"]),
            GetRequired("JurisdictionCode"), GetRequired("EffectiveRegistryJurisdictionCode"), GetRequired("UnitOfCharge"),
            GetNullable("CompanyNumber"), GetNullable("VatNumber"), GetNullable("BusinessDescription"),
            reader.GetInt32(reader.GetOrdinal("NumberOfEmployees")), GetNullable("TradingAddress"),
            GetNullable("RegisteredAddress"), GetNullable("StatutoryAddress"), versions);

        var registrations = new List<RegistrationEvidence>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            registrations.Add(new(
                GetRequired("RegistrationSchemeCode"), GetRequired("RegistrationDisplayValue"), GetBoolean("IsSensitive"),
                GetBoolean("IsReviewed"), GetRequired("ValueSourceCode"), Version("Subject.tbRegistration", "RowVer", "UpdatedOn")));

        var profiles = new List<ReportingProfileEvidence>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            profiles.Add(new(
                GetRequired("ReportingProfileCode"), GetRequired("ReportingTypeCode"), GetRequired("AuthorityCode"),
                GetNullable("TaxSourceCode"), GetNullable("AuthorityReferenceDisplay"), GetBoolean("IsReviewed"),
                GetRequired("ValueSourceCode"), Version("Cash.tbReportingProfile", "RowVer", "UpdatedOn")));

        var settings = new List<ReportingSettingEvidence>();
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            settings.Add(new(
                GetRequired("ReportingProfileCode"), GetRequired("SettingCode"), GetRequired("ValueTypeCode"),
                GetRequired("DisplayValue"), GetBoolean("IsSensitive"), GetBoolean("IsReviewed"),
                GetRequired("ValueSourceCode"), Version("Cash.tbReportingProfileSetting", "RowVer", "UpdatedOn")));

        await reader.NextResultAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("The effective business-tax reporting window could not be resolved.");
        var window = new ReportingWindow(
            DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("PayFrom"))),
            DateOnly.FromDateTime(reader.GetDateTime(reader.GetOrdinal("PayTo"))).AddDays(-1));

        return new(identity, registrations, profiles, settings, window);

        void AddVersion(string source, string rowVersionColumn, string? updatedColumn)
        {
            var ordinal = reader.GetOrdinal(rowVersionColumn);
            if (!reader.IsDBNull(ordinal))
                versions.Add(Version(source, rowVersionColumn, updatedColumn));
        }

        SourceVersion Version(string source, string rowVersionColumn, string? updatedColumn)
        {
            var bytes = (byte[])reader[rowVersionColumn];
            DateTime? updated = updatedColumn is null || reader[updatedColumn] == DBNull.Value
                ? null : Convert.ToDateTime(reader[updatedColumn]);
            return new(source, Convert.ToHexString(bytes), updated);
        }

        string GetRequired(string name) => reader[name] == DBNull.Value
            ? string.Empty : Convert.ToString(reader[name]) ?? string.Empty;
        string? GetNullable(string name) => reader[name] == DBNull.Value
            ? null : Convert.ToString(reader[name]);
        bool GetBoolean(string name) => reader[name] != DBNull.Value && Convert.ToBoolean(reader[name]);
    }
}
