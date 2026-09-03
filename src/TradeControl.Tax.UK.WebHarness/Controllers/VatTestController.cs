using Microsoft.AspNetCore.Mvc;
using TradeControl.Tax.UK.WebHarness.Requests;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Runner;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Route("harness/vat")]
public sealed class VatTestController : ControllerBase
{
    private readonly HmrcSubmissionRunner _runner;

    public VatTestController(HmrcSubmissionRunner runner)
    {
        _runner = runner;
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] HarnessRequest request, CancellationToken cancellationToken)
    {
        var result = await _runner.ExecuteAsync(
            new HmrcSubmissionRequest
            {
                OperationType = "SubmitVat",
                Parameters = new Dictionary<string, object?>
                {
                    ["taxSourceCode"] = request.TaxSourceCode,
                    ["periodEndOn"] = request.Period,
                    ["tenantId"] = request.TenantId,
                    ["subjectId"] = request.SubjectId,
                    ["connectionString"] = request.ConnectionString,
                    ["environment"] = request.Environment
                }
            },
            cancellationToken);

        return Ok(result);
    }
}
