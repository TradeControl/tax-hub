using TradeControl.Tax.UK.WebHarness.Requests.Payloads;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Mapping;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Readers;
using TradeControl.Tax.Data;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Payloads;

public sealed class MicroHarnessPayloadBuilder
{
    private static readonly string[] Tags =
    [
        "AC12",
        "AC405",
        "AC410",
        "AC415",
        "AC420",
        "AC425",
        "AC34",
        "AC435",
        "CP28",
        "CP46"
    ];

    private readonly ConnectionFactory _connections;
    private readonly TagMapper _tagMapper;

    public MicroHarnessPayloadBuilder(ConnectionFactory connections, TagMapper tagMapper)
    {
        _connections = connections;
        _tagMapper = tagMapper;
    }

    public async Task<MicroHarnessPayload> BuildAsync(
        string connectionString,
        string taxSourceCode,
        string subjectCode,
        DateTime periodTo,
        CancellationToken cancellationToken = default)
    {
        var end = DateOnly.FromDateTime(periodTo);
        var context = await new TcStatutoryContextReader(_connections, connectionString).ReadAsync(end, cancellationToken);
        var period = new TaxReportingPeriod(context.BusinessTaxWindow.Start, end, TaxPeriodKind.Cumulative,
            $"MTD-{end:yyyy-MM-dd}");
        var key = new SourceKey("harness-request");
        var reader = new TradeControlTaxSourceAdapter(_connections,
            new SourceConnectionResolver([new(key, connectionString)]));
        var source = await reader.ReadAsync(new BusinessIncomeSelector(key, new(taxSourceCode),
            new(subjectCode), period), cancellationToken);
        var items = _tagMapper.MapBusinessTaxItems(source.Facts, Tags);

        return new MicroHarnessPayload
        {
            PayloadVersion = "2026.1",
            TaxSourceCode = taxSourceCode,
            PeriodStart = source.Period.Start.ToString("yyyy-MM-dd"),
            PeriodEnd = source.Period.End.ToString("yyyy-MM-dd"),
            SubjectCode = subjectCode,
            Items = items,
            Meta = new Dictionary<string, object?>
            {
                ["operation"] = "SUBMIT_MICRO"
            }
        };
    }
}
