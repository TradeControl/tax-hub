using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Route("api/company/companies-house/accounts")]
public sealed class CompaniesHouseAccountsController(
    CompaniesHouseAccountsPayloadValidator validator,
    ICompaniesHouseAccountsRunner runner,
    ICompaniesHouseSimulationDiagnostics simulation,
    ICompaniesHouseOfficialTestPreflight officialTestPreflight,
    ICompaniesHouseOfficialTestSubmission officialTestSubmission,
    ICompaniesHouseOfficialTestStatus officialTestStatus) : ControllerBase
{
    [HttpPost]
    [Produces("application/xml", "application/json")]
    public Task<IActionResult> Prepare([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken) => Prepare(payload, false, false, cancellationToken);

    [HttpPost("document")]
    [Produces("application/xhtml+xml", "application/json")]
    public Task<IActionResult> PrepareDocument([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken) => Prepare(payload, true, false, cancellationToken);

    [HttpPost("regenerate-document")]
    [Produces("application/xhtml+xml", "application/json")]
    public Task<IActionResult> RegenerateDocument([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken) => Prepare(payload, true, true, cancellationToken);

    [HttpPost("simulate")]
    [Produces("application/json")]
    public async Task<IActionResult> Simulate([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken)
    {
        if (!simulation.Enabled)
            return NotFound(new { status = "error", code = "SIMULATOR_DISABLED",
                message = "Companies House simulation is available only in Development." });
        var validation = validator.Validate(payload);
        if (!validation.IsValid)
            return BadRequest(new { status = "error", code = "VALIDATION_ERROR",
                message = "Payload validation failed.", details = validation.Errors });
        try
        {
            return Ok(await simulation.ExecuteAsync(payload, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { status = "error", code = "SIMULATION_REQUEST_ERROR", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { status = "error", code = "SIMULATION_ERROR", message = exception.Message });
        }
        catch (SqlException)
        {
            return UnprocessableEntity(new { status = "error", code = "SOURCE_CONNECTION_ERROR",
                message = "The Trade Control source could not be read using the supplied connection." });
        }
    }

    [HttpPost("official-test-preflight")]
    [Produces("application/json")]
    public async Task<IActionResult> OfficialTestPreflight([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken)
    {
        if (!officialTestPreflight.Enabled)
            return NotFound(new { status = "error", code = "PREFLIGHT_DISABLED",
                message = "Companies House official-test preflight is available only in Development." });
        var validation = validator.Validate(payload);
        if (!validation.IsValid)
            return BadRequest(new { status = "error", code = "VALIDATION_ERROR",
                message = "Payload validation failed.", details = validation.Errors });
        try
        {
            return Ok(await officialTestPreflight.ExecuteAsync(payload, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { status = "error", code = "PREFLIGHT_REQUEST_ERROR", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { status = "error", code = "PREFLIGHT_ERROR", message = exception.Message });
        }
        catch (SqlException)
        {
            return UnprocessableEntity(new { status = "error", code = "SOURCE_CONNECTION_ERROR",
                message = "The Trade Control source could not be read using the supplied connection." });
        }
    }

    [HttpPost("official-test-submit-once")]
    [Produces("application/json")]
    public async Task<IActionResult> OfficialTestSubmitOnce([FromBody] CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken)
    {
        if (!officialTestSubmission.Enabled)
            return NotFound(new { status = "error", code = "OFFICIAL_TEST_SEND_DISABLED",
                message = "Companies House official-test submission is not enabled for this process." });
        var validation = validator.Validate(payload);
        if (!validation.IsValid)
            return BadRequest(new { status = "error", code = "VALIDATION_ERROR",
                message = "Payload validation failed.", details = validation.Errors });
        try
        {
            return Ok(await officialTestSubmission.ExecuteOnceAsync(payload, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { status = "error", code = "OFFICIAL_TEST_REQUEST_ERROR", message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { status = "error", code = "OFFICIAL_TEST_EXCHANGE_ERROR", message = exception.Message });
        }
        catch (SqlException)
        {
            return UnprocessableEntity(new { status = "error", code = "SOURCE_CONNECTION_ERROR",
                message = "The Trade Control source could not be read using the supplied connection." });
        }
    }

    [HttpPost("official-test-status-once")]
    [Produces("application/json")]
    public async Task<IActionResult> OfficialTestStatusOnce(
        [FromBody] CompaniesHouseOfficialTestStatusRequest request,
        CancellationToken cancellationToken)
    {
        if (!officialTestStatus.Enabled)
            return NotFound(new { status = "error", code = "OFFICIAL_TEST_STATUS_DISABLED",
                message = "Companies House official-test status queries are not enabled for this process." });
        try
        {
            return Ok(await officialTestStatus.ExecuteOnceAsync(request, cancellationToken));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { status = "error", code = "OFFICIAL_TEST_STATUS_REQUEST_ERROR",
                message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { status = "error", code = "OFFICIAL_TEST_STATUS_EXCHANGE_ERROR",
                message = exception.Message });
        }
        catch (InvalidDataException exception)
        {
            return UnprocessableEntity(new { status = "error", code = "OFFICIAL_TEST_STATUS_EVIDENCE_ERROR",
                message = exception.Message });
        }
    }

    private async Task<IActionResult> Prepare(CompaniesHouseAccountsPayload payload, bool document,
        bool allowTransmissionBlockers, CancellationToken cancellationToken)
    {
        var validation = validator.Validate(payload);
        if (!validation.IsValid)
            return BadRequest(new { status = "error", code = "VALIDATION_ERROR", message = "Payload validation failed.", details = validation.Errors });
        try
        {
            var package = await runner.PrepareAsync(payload, cancellationToken);
            var selected = document ? package.Documents.Single().Artifact : package.Transmission;
            if ((!allowTransmissionBlockers && package.Transmission.HasErrors)
                || package.Documents.Any(x => x.Artifact.HasErrors))
                return UnprocessableEntity(new { status = "error", code = "PREPARATION_ERROR", message = "The Companies House package did not pass preparation validation.", details = package.Transmission.Findings.Concat(package.Documents.SelectMany(x => x.Artifact.Findings)) });

            Response.Headers.ETag = $"\"{selected.Sha256}\"";
            Response.Headers.Append("X-Content-SHA256", selected.Sha256);
            Response.Headers.Append("X-Package-SHA256", package.Transmission.Sha256);
            Response.Headers.Append("X-Accounts-SHA256", package.Documents.Single().Artifact.Sha256);
            Response.Headers.Append("X-Companies-House-Contract", package.Transmission.ContractVersion);
            Response.Headers.Append("X-Polling-Mode", package.Polling.Mode.ToString());
            Response.Headers.Append("X-Polling-Path", package.Polling.RelativeStatusPath ?? string.Empty);
            if (allowTransmissionBlockers)
            {
                Response.Headers.Append("X-External-Send", "false");
                Response.Headers.Append("X-Evidence-Class", "REGENERATED LOCALLY - NOT FILED");
            }
            var fileName = document ? package.Documents.Single().Name : $"{payload.Filing.SubmissionNumber}-companies-house.xml";
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
