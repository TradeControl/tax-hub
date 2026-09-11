using Microsoft.AspNetCore.Mvc;
using TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

namespace TradeControl.Tax.UK.WebHarness.Controllers;

[ApiController]
[Route("harness/company/accounts")]
public sealed class CompanyAccountsPreviewController(CompanyAccountsPreviewService previews) : ControllerBase
{
    [HttpGet("{profile}")]
    public ActionResult Get(string profile)
    {
        if (!previews.TryGet(profile, out var preview))
            return NotFound(new { message = "Profile must be 'min-full', 'min-filleted', 'std-full' or 'std-filleted'." });

        var artifact = preview.Prepared.Artifact;
        return Ok(new
        {
            preview.Prepared.Profile,
            preview.Template,
            preview.SourceKind,
            Notice = "Synthetic demonstration data; not sourced from a Trade Control node.",
            artifact.JurisdictionCode,
            artifact.AuthorityCode,
            artifact.OperationCode,
            artifact.ContractVersion,
            Status = artifact.Status.ToString(),
            preview.Prepared.TaxonomyRelease,
            preview.Prepared.FactCount,
            preview.Prepared.FileName,
            artifact.MediaType,
            ContentLength = artifact.Content.Length,
            artifact.Sha256,
            RawPath = Url.ActionLink(nameof(GetRaw), values: new { profile = preview.Prepared.Profile }),
            artifact.SourceEvidence,
            artifact.Findings,
            artifact.HasErrors
        });
    }

    [HttpGet("{profile}/raw", Name = nameof(GetRaw))]
    [Produces("application/xhtml+xml")]
    public ActionResult GetRaw(string profile)
    {
        if (!previews.TryGet(profile, out var preview))
            return NotFound(new { message = "Profile must be 'min-full', 'min-filleted', 'std-full' or 'std-filleted'." });

        Response.Headers.ETag = $"\"{preview.Prepared.Artifact.Sha256}\"";
        Response.Headers.Append("X-Content-SHA256", preview.Prepared.Artifact.Sha256);
        return File(preview.Prepared.Artifact.Content.ToArray(), preview.Prepared.Artifact.MediaType);
    }
}
