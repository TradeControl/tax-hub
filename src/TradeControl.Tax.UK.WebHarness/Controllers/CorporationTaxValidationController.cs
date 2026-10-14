using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.Application.Preparation;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = WebHarnessAuthenticationOptions.ApplicationScheme)]
[Route("api/company/corporation-tax/validation")]
public sealed class CorporationTaxValidationController(
    CorporationTaxPayloadValidator validator,
    CorporationTaxRunner runner,
    ICorporationTaxValidationDiagnostics diagnostics) : ControllerBase
{
    [HttpPost("lts")]
    [Produces("application/json")]
    public Task<IActionResult> ValidateLts(
        [FromBody] CorporationTaxPayload payload,
        CancellationToken cancellationToken) => ValidateAsync(payload, false, cancellationToken);

    [HttpPost("tpvs")]
    [Produces("application/json")]
    public Task<IActionResult> ValidateTpvs(
        [FromBody] CorporationTaxPayload payload,
        [FromQuery] bool confirmExternalSend,
        CancellationToken cancellationToken)
    {
        if (!confirmExternalSend)
            return Task.FromResult<IActionResult>(BadRequest(new
            {
                status = "error",
                code = "EXTERNAL_SEND_CONFIRMATION_REQUIRED",
                message = "Set confirmExternalSend=true to send the prepared CT600 diagnostic envelope to HMRC TPVS."
            }));
        return ValidateAsync(payload, true, cancellationToken);
    }

    private async Task<IActionResult> ValidateAsync(
        CorporationTaxPayload payload,
        bool externalTpvs,
        CancellationToken cancellationToken)
    {
        var validation = validator.Validate(payload);
        if (!validation.IsValid)
            return BadRequest(new
            {
                status = "error",
                code = "VALIDATION_ERROR",
                message = "Payload validation failed.",
                details = validation.Errors
            });

        try
        {
            var prepared = await runner.PrepareAsync(payload, cancellationToken);
            var selected = payload.ReturnPeriodEnd is null
                ? prepared.Returns.Single()
                : prepared.Returns.Single(value => value.PeriodEnd == payload.ReturnPeriodEnd.Value);
            if (prepared.Accounts.Artifact.HasErrors || selected.Package.Transmission.HasErrors
                || selected.Package.Documents.Any(document => document.Artifact.HasErrors))
                return UnprocessableEntity(new
                {
                    status = "error",
                    code = "PREPARATION_ERROR",
                    message = "The Corporation Tax package did not pass preparation validation.",
                    details = prepared.Accounts.Artifact.Findings
                        .Concat(selected.Package.Transmission.Findings)
                        .Concat(selected.Package.Documents.SelectMany(document => document.Artifact.Findings))
                });

            var evidence = externalTpvs
                ? await diagnostics.ValidateTpvsAsync(selected.Package.Transmission, cancellationToken)
                : await diagnostics.ValidateLtsAsync(selected.Package.Transmission, cancellationToken);
            Response.Headers.ETag = $"\"{evidence.ResponseBodySha256}\"";
            Response.Headers.Append("X-CT-Service-Artifact-SHA256", evidence.ServiceArtifactSha256);
            Response.Headers.Append("X-Validation-Request-SHA256", evidence.RequestBodySha256);
            Response.Headers.Append("X-Validation-Response-SHA256", evidence.ResponseBodySha256);
            Response.Headers.Append("X-Validation-Evidence-Class", evidence.EvidenceClassification);
            return Ok(evidence);
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new
            {
                status = "error",
                code = "VALIDATION_DIAGNOSTIC_NOT_READY",
                message = exception.Message
            });
        }
        catch (SqlException)
        {
            return UnprocessableEntity(new
            {
                status = "error",
                code = "SOURCE_CONNECTION_ERROR",
                message = "The Trade Control source could not be read using the supplied connection."
            });
        }
    }
}
