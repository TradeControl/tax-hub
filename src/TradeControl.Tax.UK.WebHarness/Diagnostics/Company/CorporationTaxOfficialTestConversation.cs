using TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public sealed record CorporationTaxOfficialTestExchangeRequest(
    string CompanyNumber,
    string ConversationReference,
    string TransactionId,
    string ServiceArtifactSha256,
    string WireSha256,
    string AttemptAuthorisationReference,
    bool AuthoriseOneExternalTestExchange);

public sealed record CorporationTaxOfficialTestAdvanceRequest(
    string CompanyNumber,
    string ConversationReference,
    CorporationTaxExternalTestOperation Operation,
    string AttemptAuthorisationReference,
    bool AuthoriseOneExternalTestExchange);

public sealed record CorporationTaxOfficialTestReadRequest(
    string CompanyNumber,
    string ConversationReference);

public interface ICorporationTaxOfficialTestConversation
{
    bool Enabled { get; }
    bool ExternalExchangeEnabled { get; }
    Task<CorporationTaxExternalTestConversationOutcome> StageAsync(
        CorporationTaxPayload payload, string principalReference,
        CancellationToken cancellationToken = default);
    Task<CorporationTaxExternalTestConversationOutcome> SubmitOnceAsync(
        CorporationTaxOfficialTestExchangeRequest request, string principalReference,
        CancellationToken cancellationToken = default);
    Task<CorporationTaxExternalTestConversationOutcome> AdvanceOnceAsync(
        CorporationTaxOfficialTestAdvanceRequest request, string principalReference,
        CancellationToken cancellationToken = default);
    Task<CorporationTaxExternalTestConversationOutcome> ReadAsync(
        CorporationTaxOfficialTestReadRequest request, string principalReference,
        CancellationToken cancellationToken = default);
}

public sealed class CorporationTaxOfficialTestConversation(
    CorporationTaxRunner runner,
    CorporationTaxExternalTestConversation conversation,
    CorporationTaxOfficialTestIdentityBinder identityBinder,
    bool externalExchangeEnabled) : ICorporationTaxOfficialTestConversation
{
    public bool Enabled => true;
    public bool ExternalExchangeEnabled => externalExchangeEnabled;

    public async Task<CorporationTaxExternalTestConversationOutcome> StageAsync(
        CorporationTaxPayload payload, string principalReference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var companyNumber = CompanyNumber(payload.Identity.CompanyNumber);
        await identityBinder.BindAsync(payload, cancellationToken);
        var prepared = await runner.PrepareAsync(payload, cancellationToken);
        var selected = payload.ReturnPeriodEnd is null
            ? prepared.Returns.Single()
            : prepared.Returns.Single(value => value.PeriodEnd == payload.ReturnPeriodEnd.Value);
        if (prepared.Accounts.Artifact.HasErrors || selected.Package.Transmission.HasErrors
            || selected.Package.Documents.Any(document => document.Artifact.HasErrors))
            throw new InvalidOperationException("The immutable CT package did not pass preparation validation.");
        var eligibility = CorporationTaxOfficialTestEligibility.Evaluate(payload.OfficialTest,
            selected.Package.Transmission.Sha256, protectedConfigurationAvailable: true);
        if (!eligibility.EligibilitySatisfied)
            throw new InvalidOperationException("The CT-B official-test eligibility gate did not pass: "
                + string.Join(", ", eligibility.BlockingReasons
                    .Where(value => value is not "CT-ETS-EXTERNAL-SEND-DISABLED"
                        and not "CT-PRODUCTION-SUBMISSION-GATE-CLOSED")));
        return await conversation.StageAsync(selected.Package, payload.OfficialTest.TransactionId,
            Tenant(companyNumber), Principal(principalReference),
            $"ct:{companyNumber}:{selected.PeriodEnd:yyyy-MM-dd}:{payload.OfficialTest.TransactionId}",
            cancellationToken);
    }

    public Task<CorporationTaxExternalTestConversationOutcome> SubmitOnceAsync(
        CorporationTaxOfficialTestExchangeRequest request, string principalReference,
        CancellationToken cancellationToken = default)
    {
        RequireExchange(request.AuthoriseOneExternalTestExchange);
        return conversation.SubmitOnceAsync(Tenant(CompanyNumber(request.CompanyNumber)),
            Principal(principalReference), request.ConversationReference, request.TransactionId,
            request.ServiceArtifactSha256, request.WireSha256,
            request.AttemptAuthorisationReference, cancellationToken);
    }

    public Task<CorporationTaxExternalTestConversationOutcome> AdvanceOnceAsync(
        CorporationTaxOfficialTestAdvanceRequest request, string principalReference,
        CancellationToken cancellationToken = default)
    {
        RequireExchange(request.AuthoriseOneExternalTestExchange);
        return conversation.AdvanceOnceAsync(Tenant(CompanyNumber(request.CompanyNumber)),
            Principal(principalReference), request.ConversationReference, request.Operation,
            request.AttemptAuthorisationReference, cancellationToken);
    }

    public Task<CorporationTaxExternalTestConversationOutcome> ReadAsync(
        CorporationTaxOfficialTestReadRequest request, string principalReference,
        CancellationToken cancellationToken = default) =>
        conversation.ReadAsync(Tenant(CompanyNumber(request.CompanyNumber)),
            Principal(principalReference), request.ConversationReference, cancellationToken);

    private void RequireExchange(bool authorised)
    {
        if (!externalExchangeEnabled)
            throw new InvalidOperationException("Corporation Tax ETS exchanges are disabled for this process.");
        if (!authorised)
            throw new InvalidOperationException("The request must explicitly authorise one ETS exchange.");
    }

    private static string CompanyNumber(string? value) => value is { Length: 8 }
        && value.All(character => char.IsAsciiLetterOrDigit(character))
            ? value.ToUpperInvariant()
            : throw new ArgumentException("An eight-character company number is required.");

    private static string Tenant(string companyNumber) => $"ct-company:{companyNumber}";

    private static string Principal(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
        ? value : throw new ArgumentException("An authenticated principal reference is required.");
}

public sealed class DisabledCorporationTaxOfficialTestConversation : ICorporationTaxOfficialTestConversation
{
    public bool Enabled => false;
    public bool ExternalExchangeEnabled => false;

    public Task<CorporationTaxExternalTestConversationOutcome> StageAsync(
        CorporationTaxPayload payload, string principalReference,
        CancellationToken cancellationToken = default) => Disabled();
    public Task<CorporationTaxExternalTestConversationOutcome> SubmitOnceAsync(
        CorporationTaxOfficialTestExchangeRequest request, string principalReference,
        CancellationToken cancellationToken = default) => Disabled();
    public Task<CorporationTaxExternalTestConversationOutcome> AdvanceOnceAsync(
        CorporationTaxOfficialTestAdvanceRequest request, string principalReference,
        CancellationToken cancellationToken = default) => Disabled();
    public Task<CorporationTaxExternalTestConversationOutcome> ReadAsync(
        CorporationTaxOfficialTestReadRequest request, string principalReference,
        CancellationToken cancellationToken = default) => Disabled();

    private static Task<CorporationTaxExternalTestConversationOutcome> Disabled() =>
        throw new InvalidOperationException("Corporation Tax ETS conversations are disabled.");
}
