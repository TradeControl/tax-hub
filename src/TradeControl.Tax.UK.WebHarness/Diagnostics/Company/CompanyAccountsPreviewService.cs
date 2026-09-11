using TradeControl.Tax.UK.Application.DataProvision;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed record CompanyAccountsPreview(
    string SourceKind,
    string Template,
    PreparedCompanyAccounts Prepared);

public sealed class CompanyAccountsPreviewService
{
    private const string SourceKind = "SyntheticFixture";
    private readonly IReadOnlyDictionary<string, CompanyAccountsPreview> previews;

    public CompanyAccountsPreviewService()
    {
        var preparer = new CompanyAccountsPreparer();
        var populator = new CompanyAccountsPopulator();

        previews = new[] { "MIN", "STD" }
            .SelectMany(template => new[] { false, true }.Select(filleted =>
            {
                var profile = filleted ? "filleted" : "full";
                var source = CompanyAccountsSourceFixture.Create(template);
                var accounts = populator.Populate(source, new(true, true));
                var key = $"{template.ToLowerInvariant()}-{profile}";
                var fileName = $"synthetic-{key}-accounts.xhtml";
                var prepared = preparer.Prepare(
                    accounts,
                    filleted,
                    $"Synthetic {profile} accounts preview",
                    fileName,
                    source.Versions);
                return new CompanyAccountsPreview(
                    SourceKind,
                    template,
                    prepared);
            }))
            .ToDictionary(preview => $"{preview.Template}-{preview.Prepared.Profile}", StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGet(string profile, out CompanyAccountsPreview preview) =>
        previews.TryGetValue(profile, out preview!);

}
