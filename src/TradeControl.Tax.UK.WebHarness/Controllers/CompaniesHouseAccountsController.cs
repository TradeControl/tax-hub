using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Route("api/company/companies-house/accounts")]
public sealed class CompaniesHouseAccountsController(
    CompaniesHouseAccountsPayloadValidator validator,
    CompaniesHouseAccountsRunner runner) : ControllerBase
{
    [HttpPost]
    [Produces("application/xml", "application/json")]
    public Task<IActionResult> Prepare([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken) => Prepare(payload, false, cancellationToken);

    [HttpPost("document")]
    [Produces("application/xhtml+xml", "application/json")]
    public Task<IActionResult> PrepareDocument([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken) => Prepare(payload, true, cancellationToken);

    private async Task<IActionResult> Prepare(CompaniesHouseAccountsPayload payload, bool document,
        CancellationToken cancellationToken)
    {
        var validation = validator.Validate(payload);
        if (!validation.IsValid)
            return BadRequest(new { status = "error", code = "VALIDATION_ERROR", message = "Payload validation failed.", details = validation.Errors });
        try
        {
            var package = await runner.PrepareAsync(payload, cancellationToken);
            var selected = document ? package.Documents.Single().Artifact : package.Transmission;
            if (package.Transmission.HasErrors || package.Documents.Any(x => x.Artifact.HasErrors))
                return UnprocessableEntity(new { status = "error", code = "PREPARATION_ERROR", message = "The Companies House package did not pass preparation validation.", details = package.Transmission.Findings.Concat(package.Documents.SelectMany(x => x.Artifact.Findings)) });

            Response.Headers.ETag = $"\"{selected.Sha256}\"";
            Response.Headers.Append("X-Content-SHA256", selected.Sha256);
            Response.Headers.Append("X-Package-SHA256", package.Transmission.Sha256);
            Response.Headers.Append("X-Accounts-SHA256", package.Documents.Single().Artifact.Sha256);
            Response.Headers.Append("X-Companies-House-Contract", package.Transmission.ContractVersion);
            Response.Headers.Append("X-Polling-Mode", package.Polling.Mode.ToString());
            Response.Headers.Append("X-Polling-Path", package.Polling.RelativeStatusPath ?? string.Empty);
            var fileName = document ? package.Documents.Single().Name : $"{payload.Filing.EnvelopeNumber}-companies-house.xml";
            return File(selected.Content.ToArray(), selected.MediaType, fileName);
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { status = "error", code = "SOURCE_NOT_READY", message = exception.Message });
        }
        catch (SqlException)
        {
            return UnprocessableEntity(new { status = "error", code = "SOURCE_CONNECTION_ERROR", message = "The Trade Control source could not be read using the supplied connection." });
        }
    }
}
