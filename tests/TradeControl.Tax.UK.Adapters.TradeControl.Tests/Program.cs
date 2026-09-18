using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;
using Microsoft.Data.SqlClient;

var assertions = 0;
void Assert(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}

var sourceKey = new SourceKey("sole-trader-standard");
var resolver = new SourceConnectionResolver([new(sourceKey, "synthetic-connection")]);
Assert(resolver.Resolve(sourceKey) == "synthetic-connection", "A configured source key was not resolved.");
try { _ = resolver.Resolve(new("unknown")); Assert(false, "An unknown source was accepted."); }
catch (KeyNotFoundException) { Assert(true, "Unknown sources fail closed."); }

var context = Context();
var subject = TradeControlSourceMapper.Subject(context);
Assert(subject.LegalForm == TaxLegalForm.SoleTrader && subject.SubjectKind == TaxSubjectKind.Person,
    "The statutory context was not translated to neutral subject semantics.");

var vatPeriod = new TaxReportingPeriod(new(2026, 4, 1), new(2026, 6, 30), TaxPeriodKind.Vat, "26-Q1");
var vat = TradeControlSourceMapper.Vat(subject, vatPeriod,
    new(vatPeriod.Start, vatPeriod.End, -10.12345m, 0m, 0m, -10.12345m, 2m, -12.12345m, 100m, 20m, 0m, 0m), "0x01");
Assert(vat.VatDueSales.Value.Value == -10.12345m && vat.VatDueAcquisitions.Value.State == TaxValueState.ExplicitZero,
    "VAT translation changed polarity, precision or explicit zero.");
try
{
    _ = TradeControlSourceMapper.Vat(subject, vatPeriod,
        new(new(2026, 4, 2), vatPeriod.End, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), "0x02");
    Assert(false, "A mismatched VAT period was accepted.");
}
catch (InvalidOperationException) { Assert(true, "VAT period mismatch fails closed."); }

var period = new TaxReportingPeriod(new(2026, 4, 6), new(2026, 7, 5), TaxPeriodKind.Cumulative, "26-Q1");
var source = new TaxSourceCode("UK-ITSA-SE-CUM");
var rows = new[]
{
    new TcBusinessProjectionRow(source.Value, period.Start, period.End.AddDays(1), "Ready", "TURNOVER", "Turnover", 1, "Supported", 1200.12345m),
    new TcBusinessProjectionRow(source.Value, period.Start, period.End.AddDays(1), "Ready", "COSTS", "Costs", 0, "Supported", 0m),
    new TcBusinessProjectionRow(source.Value, period.Start, period.End.AddDays(1), "Ready", "OTHER", "Other", 0, "Unsupported", null),
    new TcBusinessProjectionRow(source.Value, period.Start, period.End.AddDays(1), "Invalid", "TRAVEL", "Travel", 0, "Invalid", null)
};
var contributors = new[] { new TcContributorRow("TURNOVER", "SALE", period.Start, "01", "02") };
var business = TradeControlSourceMapper.Business(subject, new("XAIS12345678901"), source, period, rows, contributors);
Assert(business.Facts[0].Amount.Value == 1200.12345m && business.Facts[0].Kind == TaxFactKind.Income,
    "Cumulative translation changed authoritative amount or polarity.");
Assert(business.Facts[1].Amount.State == TaxValueState.ExplicitZero
    && business.Facts[2].Amount.State == TaxValueState.Unsupported
    && business.Facts[3].Amount.State == TaxValueState.Invalid,
    "Cumulative source states were collapsed.");
Assert(business.Facts[0].DisplayLabel == "Turnover" && business.Facts[0].Provenance.MappingKey == "SALE"
    && business.Provenance.SnapshotToken.Length == 64, "Labels or contributor provenance were not preserved.");

try
{
    _ = TradeControlSourceMapper.Business(subject, business.BusinessId, source, period, [rows[0], rows[0]], contributors);
    Assert(false, "Duplicate cumulative facts were accepted.");
}
catch (InvalidOperationException) { Assert(true, "Duplicate cumulative facts fail closed."); }
try
{
    var wrong = rows[0] with { PeriodEndExclusive = period.End };
    _ = TradeControlSourceMapper.Business(subject, business.BusinessId, source, period, [wrong], contributors);
    Assert(false, "An inclusive SQL end boundary was accepted.");
}
catch (InvalidOperationException) { Assert(true, "Exclusive SQL end boundary is enforced."); }

var connectionFileIndex = Array.IndexOf(args, "--connection-file");
var connectionEnvironmentIndex = Array.IndexOf(args, "--connection-environment");
if (connectionFileIndex >= 0 || connectionEnvironmentIndex >= 0)
{
    string connectionString;
    if (connectionFileIndex >= 0)
    {
        if (connectionFileIndex + 1 >= args.Length) throw new ArgumentException("--connection-file requires a path.");
        connectionString = File.ReadAllText(args[connectionFileIndex + 1]).Trim();
    }
    else
    {
        if (connectionEnvironmentIndex + 1 >= args.Length)
            throw new ArgumentException("--connection-environment requires a variable name.");
        connectionString = Environment.GetEnvironmentVariable(args[connectionEnvironmentIndex + 1])
            ?? throw new InvalidOperationException("The requested connection environment variable is not set.");
    }
    var liveKey = new SourceKey("sandbox");
    var factory = new ConnectionFactory();
    var adapter = new TradeControlTaxSourceAdapter(factory,
        new SourceConnectionResolver([new(liveKey, connectionString)]));

    await using var connection = new SqlConnection(connectionString);
    await connection.OpenAsync();
    await using (var optionsCommand = new SqlCommand("SELECT COUNT(*) FROM App.tbOptions", connection))
    {
        var optionsCount = Convert.ToInt32(await optionsCommand.ExecuteScalarAsync());
        if (optionsCount == 0) throw new InvalidOperationException("The configured sandbox contains no App.tbOptions row.");
    }
    var asOfDate = DateOnly.FromDateTime(DateTime.Today);
    await using (var command = new SqlCommand("""
        SELECT TOP (1)
               CONVERT(date, due.PayFrom),
               CONVERT(date, DATEADD(day, -1, due.PayTo)),
               CONVERT(date, submission.VatEndOn)
        FROM Cash.vwTaxVatSubmission submission
        JOIN Cash.fnTaxTypeDueDates(1, 0) due ON submission.StartOn = due.PayTo
        ORDER BY submission.StartOn DESC
        """, connection))
    await using (var reader = await command.ExecuteReaderAsync())
    {
        if (await reader.ReadAsync())
        {
            var livePeriod = new TaxReportingPeriod(DateOnly.FromDateTime(reader.GetDateTime(0)),
                DateOnly.FromDateTime(reader.GetDateTime(1)), TaxPeriodKind.Vat, "sandbox-vat");
            var selectionPeriod = new TaxReportingPeriod(
                new DateOnly(reader.GetDateTime(2).Year, reader.GetDateTime(2).Month, 1),
                DateOnly.FromDateTime(reader.GetDateTime(2)), TaxPeriodKind.Vat, "sandbox-vat");
            Console.WriteLine($"Sandbox VAT period: {livePeriod.Start:yyyy-MM-dd} to {livePeriod.End:yyyy-MM-dd}.");
            asOfDate = livePeriod.End;
            var liveVat = await adapter.ReadAsync(new VatReturnSelector(liveKey, selectionPeriod));
            Assert(liveVat.Period == livePeriod && liveVat.Provenance.Facts.Count == 10,
                "The sandbox VAT projection did not cross the neutral adapter intact.");
            var alternateStart = new TaxReportingPeriod(selectionPeriod.Start.AddDays(-1), selectionPeriod.End,
                TaxPeriodKind.Vat, selectionPeriod.StableKey);
            var endSelectedVat = await adapter.ReadAsync(new VatReturnSelector(liveKey, alternateStart));
            Assert(endSelectedVat.Period == livePeriod,
                "VAT selection incorrectly depended on StartOn instead of VatEndOn.");
            var readiness = await adapter.EvaluateAsync(new SourceReadinessRequest(liveKey, liveVat.Subject, livePeriod));
            Assert(readiness.Findings.All(item => !string.IsNullOrWhiteSpace(item.Code)),
                "The sandbox readiness projection returned an unstructured finding.");
        }
    }

    var statutory = await new TcStatutoryContextReader(factory, connectionString).ReadAsync(asOfDate);
    string? businessTaxSource;
    await using (var sourceCommand = new SqlCommand(
        "SELECT TaxSourceCode FROM Cash.tbTaxTagSource WHERE TaxSourceCode = N'UK-ITSA-SE-CUM'", connection))
        businessTaxSource = Convert.ToString(await sourceCommand.ExecuteScalarAsync());
    if (statutory.Identity.BusinessTaxTypeCode == 4)
    {
        if (string.IsNullOrWhiteSpace(businessTaxSource))
        {
            await using var inventoryCommand = new SqlCommand(
                "SELECT STRING_AGG(CONCAT(TaxSourceCode, N':', TaxTypeCode), N',') FROM Cash.tbTaxTagSource", connection);
            var inventory = Convert.ToString(await inventoryCommand.ExecuteScalarAsync()) ?? "none";
            throw new InvalidOperationException(
                $"The sole-trader sandbox has no UK-ITSA-SE-CUM Tax Tag source. Available sources: {inventory}.");
        }
        var livePeriod = new TaxReportingPeriod(statutory.BusinessTaxWindow.Start, statutory.BusinessTaxWindow.End,
            TaxPeriodKind.Cumulative, "sandbox-business");
        var liveBusiness = await adapter.ReadAsync(new BusinessIncomeSelector(liveKey,
            new(businessTaxSource), new("sandbox-business"), livePeriod));
        Assert(liveBusiness.Facts.Count > 0 && liveBusiness.Provenance.Facts.Count == liveBusiness.Facts.Count,
            "The sandbox cumulative projection did not preserve its neutral facts and provenance.");
        var readiness = await adapter.EvaluateAsync(new SourceReadinessRequest(
            liveKey, liveBusiness.Subject, livePeriod, liveBusiness.TaxSourceCode));
        Assert(readiness.Findings.All(item => !string.IsNullOrWhiteSpace(item.Code)),
            "The sandbox business-readiness projection returned an unstructured finding.");
    }
}

Console.WriteLine($"Trade Control adapter tests passed ({assertions} assertions).");

static StatutoryContextSnapshot Context()
{
    var version = new SourceVersion("fixture", "01", null);
    return new(new("HOME", "Example Trader", 4, "UK", "UK", "GBP", null, null, "Trade", 1,
            "Trading", null, "Trading", [version]), [], [], [],
        new(new(2026, 4, 6), new(2027, 4, 5)));
}
