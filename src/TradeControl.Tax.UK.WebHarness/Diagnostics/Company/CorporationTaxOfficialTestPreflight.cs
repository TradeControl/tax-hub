using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public interface ICorporationTaxOfficialTestPreflight
{
    bool Enabled { get; }
    Task<CorporationTaxOfficialTestPreflightOutcome> ExecuteAsync(
        CorporationTaxPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed record CorporationTaxOfficialTestPreflightOutcome(
    string EvidenceClass,
    string MessageClass,
    string IntendedEndpoint,
    string TransactionId,
    string ReturnPeriod,
    string ServiceArtifactSha256,
    string WireSha256,
    string IrmarkSha256,
    IReadOnlyList<string> AttachmentSha256,
    bool ProtectedConfigurationAvailable,
    bool CredentialedEnvelopeMaterialized,
    bool BaselineApprovalDeclared,
    bool ExactArtifactApproved,
    bool NamedAttemptAuthorised,
    bool OfficialTestEligibilitySatisfied,
    bool ExternalSendEnabled,
    IReadOnlyList<string> BlockingReasons);

public static class CorporationTaxOfficialTestEligibility
{
    public static CorporationTaxOfficialTestEligibilityOutcome Evaluate(
        CorporationTaxOfficialTestPayload officialTest,
        string serviceArtifactSha256,
        bool protectedConfigurationAvailable)
    {
        ArgumentNullException.ThrowIfNull(officialTest);
        var approval = officialTest.Approval ?? new CorporationTaxOfficialTestApprovalPayload();
        var baseline = BoundedReference(approval.BaselineApprovalReference);
        var exactArtifact = IsSha256(approval.ApprovedServiceArtifactSha256)
            && FixedEquals(approval.ApprovedServiceArtifactSha256, serviceArtifactSha256);
        var namedAttempt = BoundedReference(approval.AttemptAuthorisationReference)
            && string.Equals(approval.AuthorisedTransactionId, officialTest.TransactionId,
                StringComparison.Ordinal);
        var blockers = new List<string> { "CT-ETS-EXTERNAL-SEND-DISABLED" };
        if (!baseline) blockers.Add("CT-ETS-BASELINE-APPROVAL-MISSING");
        if (!exactArtifact) blockers.Add("CT-ETS-EXACT-ARTIFACT-APPROVAL-MISSING-OR-MISMATCHED");
        if (!protectedConfigurationAvailable) blockers.Add("CT-ETS-PROTECTED-CONFIGURATION-UNAVAILABLE");
        if (!namedAttempt) blockers.Add("CT-ETS-NAMED-ATTEMPT-AUTHORISATION-MISSING-OR-MISMATCHED");
        blockers.Add("CT-PRODUCTION-SUBMISSION-GATE-CLOSED");
        return new(baseline, exactArtifact, namedAttempt,
            baseline && exactArtifact && protectedConfigurationAvailable && namedAttempt,
            blockers);
    }

    private static bool BoundedReference(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128 && value.All(character => !char.IsControl(character));

    private static bool IsSha256(string? value) => value?.Length == 64
        && value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool FixedEquals(string left, string right) => IsSha256(right)
        && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left), Convert.FromHexString(right));
}

public sealed record CorporationTaxOfficialTestEligibilityOutcome(
    bool BaselineApprovalDeclared,
    bool ExactArtifactApproved,
    bool NamedAttemptAuthorised,
    bool EligibilitySatisfied,
    IReadOnlyList<string> BlockingReasons);

public sealed class CorporationTaxOfficialTestIdentityBinder(ISecretProvider secrets)
{
    public async Task BindAsync(CorporationTaxPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        using var testUtr = await secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.Utr, cancellationToken);
        var characters = new char[testUtr.Length];
        try
        {
            testUtr.CopyTo(characters);
            payload.Identity.Utr = new string(characters);
        }
        finally
        {
            Array.Clear(characters);
        }
    }
}

public sealed class CorporationTaxOfficialTestPreflight(
    CorporationTaxRunner runner,
    CorporationTaxOfficialTestMaterializer materializer,
    CorporationTaxOfficialTestIdentityBinder identityBinder,
    string product,
    string productVersion) : ICorporationTaxOfficialTestPreflight
{
    public bool Enabled => true;

    public async Task<CorporationTaxOfficialTestPreflightOutcome> ExecuteAsync(
        CorporationTaxPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await identityBinder.BindAsync(payload, cancellationToken);
        var transactionId = payload.OfficialTest.TransactionId;
        if (transactionId.Length is < 1 or > 32
            || transactionId.Any(character =>
                !(character is >= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new ArgumentException(
                "A one-to-32-character upper-case hexadecimal officialTest.transactionId is required.",
                nameof(payload));

        var prepared = await runner.PrepareAsync(payload, cancellationToken);
        var selected = payload.ReturnPeriodEnd is null
            ? prepared.Returns.Single()
            : prepared.Returns.Single(value => value.PeriodEnd == payload.ReturnPeriodEnd.Value);
        if (prepared.Accounts.Artifact.HasErrors || selected.Package.Transmission.HasErrors
            || selected.Package.Documents.Any(document => document.Artifact.HasErrors))
            throw new InvalidOperationException("The immutable CT package did not pass preparation validation.");

        var materialized = await materializer.MaterializeAsync(
            selected.Package, transactionId, product, productVersion, cancellationToken);
        var eligibility = CorporationTaxOfficialTestEligibility.Evaluate(
            payload.OfficialTest, materialized.ServiceArtifactSha256, true);
        return new(
            materialized.EvidenceClass,
            materialized.MessageClass,
            materialized.IntendedEndpoint,
            materialized.TransactionId,
            $"{selected.PeriodStart:yyyy-MM-dd}/{selected.PeriodEnd:yyyy-MM-dd}",
            materialized.ServiceArtifactSha256,
            materialized.WireSha256,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.ASCII.GetBytes(materialized.Irmark))),
            materialized.AttachmentSha256,
            true,
            true,
            eligibility.BaselineApprovalDeclared,
            eligibility.ExactArtifactApproved,
            eligibility.NamedAttemptAuthorised,
            eligibility.EligibilitySatisfied,
            false,
            eligibility.BlockingReasons);
    }
}

public sealed class DisabledCorporationTaxOfficialTestPreflight : ICorporationTaxOfficialTestPreflight
{
    public bool Enabled => false;

    public Task<CorporationTaxOfficialTestPreflightOutcome> ExecuteAsync(
        CorporationTaxPayload payload,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Corporation Tax ETS preflight is available only in Development.");
}
