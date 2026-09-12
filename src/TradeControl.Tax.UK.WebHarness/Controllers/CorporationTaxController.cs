using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Route("api/company/corporation-tax")]
public sealed class CorporationTaxController(
    CorporationTaxPayloadValidator validator,
    CorporationTaxRunner runner) : ControllerBase
{
    [HttpPost]
    [Produces("application/xml", "application/json")]
    public async Task<IActionResult> Prepare(
        [FromBody] CorporationTaxPayload payload,
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

            var computation = selected.Package.Documents.Single(document =>
                document.Artifact.OperationCode == "CORPORATION-TAX-COMPUTATION");
            Response.Headers.ETag = $"\"{selected.Package.Transmission.Sha256}\"";
            Response.Headers.Append("X-Content-SHA256", selected.Package.Transmission.Sha256);
            Response.Headers.Append("X-Accounts-SHA256", prepared.Accounts.Artifact.Sha256);
            Response.Headers.Append("X-Computation-SHA256", computation.Artifact.Sha256);
            Response.Headers.Append("X-CT600-RIM-Version", selected.Package.Transmission.ContractVersion);
            Response.Headers.Append("X-Return-Period", $"{selected.PeriodStart:yyyy-MM-dd}/{selected.PeriodEnd:yyyy-MM-dd}");
            return File(selected.Package.Transmission.Content.ToArray(), selected.Package.Transmission.MediaType,
                $"{payload.Identity.CompanyNumber ?? "company"}-{selected.PeriodEnd:yyyyMMdd}-ct600.xml");
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new { status = "error", code = "SOURCE_NOT_READY", message = exception.Message });
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
