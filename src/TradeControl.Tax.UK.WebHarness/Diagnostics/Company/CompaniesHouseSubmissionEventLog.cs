using System.Data;
using System.Text;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.CompaniesHouse.Accounts.Tis6_0;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public interface ICompaniesHouseSubmissionEventLog
{
    Task<string> WriteAsync(
        string sqlConnection,
        PreparedSubmissionPackage package,
        string submissionNumber,
        string transactionId,
        string responseKind,
        string requestSha256,
        string? responseSha256,
        IReadOnlyList<CompaniesHouseError> errors,
        bool isError,
        CancellationToken cancellationToken = default);
}

public sealed class CompaniesHouseSubmissionEventLog : ICompaniesHouseSubmissionEventLog
{
    private const short ErrorEventType = 0;
    private const short InformationEventType = 2;

    public async Task<string> WriteAsync(
        string sqlConnection,
        PreparedSubmissionPackage package,
        string submissionNumber,
        string transactionId,
        string responseKind,
        string requestSha256,
        string? responseSha256,
        IReadOnlyList<CompaniesHouseError> errors,
        bool isError,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sqlConnection))
            throw new ArgumentException("A Trade Control connection is required for submission event logging.",
                nameof(sqlConnection));

        var accounts = package.Documents.Single().Artifact;
        if (!string.Equals(accounts.MediaType, "application/xhtml+xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The Companies House submission event payload is not iXBRL XHTML.");

        var message = BuildMessage(accounts.Content.ToArray(), accounts.Sha256, submissionNumber,
            transactionId, responseKind, requestSha256, responseSha256, errors);
        await using var connection = new SqlConnection(sqlConnection);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "App.proc_EventLog";
        command.CommandType = CommandType.StoredProcedure;
        command.Parameters.Add(new SqlParameter("@EventMessage", SqlDbType.NVarChar, -1)
        {
            Value = message
        });
        command.Parameters.Add(new SqlParameter("@EventTypeCode", SqlDbType.SmallInt)
        {
            Value = isError ? ErrorEventType : InformationEventType
        });
        var logCode = new SqlParameter("@LogCode", SqlDbType.NVarChar, 20)
        {
            Direction = ParameterDirection.Output
        };
        command.Parameters.Add(logCode);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return logCode.Value as string
            ?? throw new InvalidDataException("App.proc_EventLog did not return a log code.");
    }

    internal static string BuildMessage(
        ReadOnlySpan<byte> accounts,
        string accountsSha256,
        string submissionNumber,
        string transactionId,
        string responseKind,
        string requestSha256,
        string? responseSha256,
        IReadOnlyList<CompaniesHouseError> errors)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Companies House accounts submission")
            .Append("Submission number: ").AppendLine(submissionNumber)
            .Append("Transaction ID: ").AppendLine(transactionId)
            .Append("Response: ").AppendLine(responseKind)
            .Append("Request SHA-256: ").AppendLine(requestSha256)
            .Append("Response SHA-256: ").AppendLine(responseSha256 ?? "Unavailable")
            .Append("Accounts SHA-256: ").AppendLine(accountsSha256);
        foreach (var error in errors)
            builder.Append("Error ").Append(error.Code).Append(": ").AppendLine(error.Message);
        builder.AppendLine("Accounts payload (exact iXBRL sent; transport credentials excluded):")
            .Append(Encoding.UTF8.GetString(accounts));
        return builder.ToString();
    }
}
