using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Route("api/company/accounts")]
public sealed class CompanyAccountsController(
    CompanyAccountsPayloadValidator validator,
    CompanyAccountsRunner runner) : ControllerBase
{
    [HttpPost]
    [Produces("application/xhtml+xml", "application/json")]
    public async Task<IActionResult> Prepare(
        [FromBody] CompanyAccountsPayload payload,
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
            if (prepared.Artifact.HasErrors)
                return UnprocessableEntity(new
                {
                    status = "error",
                    code = "PREPARATION_ERROR",
                    message = "The statutory accounts did not pass preparation validation.",
                    details = prepared.Artifact.Findings
                });

            Response.Headers.ETag = $"\"{prepared.Artifact.Sha256}\"";
            Response.Headers.Append("X-Content-SHA256", prepared.Artifact.Sha256);
            Response.Headers.Append("X-Taxonomy-Release", prepared.TaxonomyRelease);
            Response.Headers.Append("X-Fact-Count", prepared.FactCount.ToString());
            return File(prepared.Artifact.Content.ToArray(), prepared.Artifact.MediaType, prepared.FileName);
        }
        catch (InvalidOperationException exception)
        {
            return UnprocessableEntity(new
            {
                status = "error",
                code = "SOURCE_NOT_READY",
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
