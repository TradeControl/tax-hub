using Microsoft.AspNetCore.Mvc;
using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

public sealed record PrepareCumulativeSummaryRequest(
    string ConnectionString,
    string TaxSourceCode,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string TaxYear);

[ApiController]
[Route("harness/hmrc/mtd-income-tax/cumulative")]
public sealed class CumulativePreparationController : ControllerBase
{
    private readonly ConnectionFactory _connections;
    private readonly PreparedApiRequestPipeline _pipeline;
    private readonly PreparedApiRequestStore _store;

    public CumulativePreparationController(ConnectionFactory connections,
        PreparedApiRequestPipeline pipeline, PreparedApiRequestStore store)
    {
        _connections = connections;
        _pipeline = pipeline;
        _store = store;
    }

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare([FromBody] PrepareCumulativeSummaryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return BadRequest(new { error = "connectionString is required." });
        TaxSourceCode taxSource;
        TaxReportingPeriod period;
        try
        {
            taxSource = new(request.TaxSourceCode);
            period = new(request.PeriodStart, request.PeriodEnd, TaxPeriodKind.Cumulative,
                $"{request.TaxYear}:{request.PeriodEnd:yyyy-MM-dd}");
        }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }

        var sourceKey = new SourceKey("harness-request");
        var resolver = new SourceConnectionResolver([new(sourceKey, request.ConnectionString)]);
        var source = new TradeControlTaxSourceAdapter(_connections, resolver);
        var statutory = new TradeControlStatutoryContextAdapter(_connections, resolver);
        var filing = new TradeControlSelfEmploymentFilingContextAdapter(_connections, resolver);
        var preparer = new CumulativePeriodSummaryPreparer(source, source, statutory, filing, _pipeline);
        var prepared = await preparer.PrepareAsync(
            new(sourceKey, taxSource, period, request.TaxYear), cancellationToken);
        var id = await _store.SaveAsync(prepared, cancellationToken);
        var inspection = PreparedApiRequestInspection.From(id, prepared);
        return prepared.HasErrors ? UnprocessableEntity(inspection) : Ok(inspection);
    }

    [HttpGet("{preparationId}")]
    public IActionResult Inspect(string preparationId) => _store.TryGet(preparationId, out var request)
        ? Ok(PreparedApiRequestInspection.From(preparationId, request)) : NotFound();

    [HttpGet("{preparationId}/body")]
    public async Task<IActionResult> Body(string preparationId, CancellationToken cancellationToken)
    {
        if (!_store.TryGet(preparationId, out var request)) return NotFound();
        if (request.BodyBytes is not { } body) return UnprocessableEntity(
            PreparedApiRequestInspection.From(preparationId, request));
        await PreparedApiRequestHttp.WriteBodyAsync(Response, request, cancellationToken);
        return new EmptyResult();
    }
}
