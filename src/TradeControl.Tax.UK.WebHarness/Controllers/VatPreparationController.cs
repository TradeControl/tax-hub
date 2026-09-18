using Microsoft.AspNetCore.Mvc;
using TradeControl.Tax.Data;
using TradeControl.Tax.UK.Adapters.TradeControl.Data;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

public sealed record PrepareVatReturnRequest(
    string ConnectionString,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string PeriodKey,
    bool Finalised = true,
    string? VatRegistrationOverride = null);

[ApiController]
[Route("harness/hmrc/vat/returns")]
public sealed class VatPreparationController : ControllerBase
{
    private readonly ConnectionFactory _connections;
    private readonly PreparedApiRequestPipeline _pipeline;
    private readonly VatPreparationStore _store;

    public VatPreparationController(ConnectionFactory connections, PreparedApiRequestPipeline pipeline,
        VatPreparationStore store)
    {
        _connections = connections;
        _pipeline = pipeline;
        _store = store;
    }

    [HttpPost("prepare")]
    public async Task<IActionResult> Prepare([FromBody] PrepareVatReturnRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ConnectionString))
            return BadRequest(new { error = "connectionString is required." });
        TaxReportingPeriod period;
        try { period = new(request.PeriodStart, request.PeriodEnd, TaxPeriodKind.Vat,
            $"VAT-{request.PeriodEnd:yyyy-MM-dd}"); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }

        var sourceKey = new SourceKey("harness-request");
        var resolver = new SourceConnectionResolver([new(sourceKey, request.ConnectionString)]);
        var source = new TradeControlTaxSourceAdapter(_connections, resolver);
        var context = new TradeControlStatutoryContextAdapter(_connections, resolver);
        var preparer = new VatReturnPreparer(source, source, context, _pipeline);
        var prepared = await preparer.PrepareAsync(
            new(sourceKey, period, request.PeriodKey, request.Finalised,
                request.VatRegistrationOverride), cancellationToken);
        var id = await _store.SaveAsync(prepared, cancellationToken);
        var inspection = VatPreparationInspection.From(id, prepared);
        return prepared.HasErrors ? UnprocessableEntity(inspection) : Ok(inspection);
    }

    [HttpGet("{preparationId}")]
    public IActionResult Inspect(string preparationId) => _store.TryGet(preparationId, out var request)
        ? Ok(VatPreparationInspection.From(preparationId, request)) : NotFound();

    [HttpGet("{preparationId}/body")]
    public IActionResult Body(string preparationId)
    {
        if (!_store.TryGet(preparationId, out var request)) return NotFound();
        if (request.BodyBytes is not { } body) return UnprocessableEntity(
            VatPreparationInspection.From(preparationId, request));
        Response.Headers["X-TaxHub-Preview"] = "true";
        return File(body.ToArray(), request.ContentType ?? "application/octet-stream");
    }
}
