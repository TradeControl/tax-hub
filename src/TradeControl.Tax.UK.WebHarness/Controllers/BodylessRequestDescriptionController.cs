using Microsoft.AspNetCore.Mvc;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

public sealed record BodylessRequestDescription(
    string OperationId,
    string ContractFamily,
    string ContractVersion,
    string Method,
    string RelativePath,
    IReadOnlyList<PreparedNameValue> Query,
    IReadOnlyList<PreparedNameValue> Headers)
{
    public static BodylessRequestDescription From(PreparedApiRequest request) => new(
        request.OperationId, request.ContractFamily, request.ContractVersion, request.Method,
        request.RelativePath, request.Query, request.Headers);
}

[ApiController]
[Route("harness/hmrc")]
public sealed class BodylessRequestDescriptionController(BodylessRequestDescriber describer) : ControllerBase
{
    [HttpPost("vat/obligations/describe")]
    public IActionResult DescribeVatObligations([FromBody] DescribeVatObligations request) =>
        Describe(() => describer.Describe(request));

    [HttpPost("vat/returns/view/describe")]
    public IActionResult DescribeVatReturn([FromBody] DescribeVatReturn request) =>
        Describe(() => describer.Describe(request));

    [HttpPost("mtd-income-tax/obligations/describe")]
    public IActionResult DescribeIncomeTaxObligations([FromBody] DescribeIncomeTaxObligations request) =>
        Describe(() => describer.Describe(request));

    private IActionResult Describe(Func<PreparedApiRequest> action)
    {
        try { return Ok(BodylessRequestDescription.From(action())); }
        catch (ArgumentException exception) { return BadRequest(new { error = exception.Message }); }
        catch (InvalidOperationException exception) { return BadRequest(new { error = exception.Message }); }
    }
}
