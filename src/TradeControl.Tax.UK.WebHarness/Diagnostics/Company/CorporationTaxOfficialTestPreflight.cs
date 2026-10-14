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
    bool ExternalSendEnabled,
    IReadOnlyList<string> BlockingReasons);

public sealed class CorporationTaxOfficialTestPreflight(
    CorporationTaxRunner runner,
    CorporationTaxOfficialTestMaterializer materializer,
    string product,
    string productVersion) : ICorporationTaxOfficialTestPreflight
{
    public bool Enabled => true;

    public async Task<CorporationTaxOfficialTestPreflightOutcome> ExecuteAsync(
        CorporationTaxPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
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
            false,
            [
                "CT-ETS-EXTERNAL-SEND-DISABLED",
                "CT-RECOGNITION-EVIDENCE-PENDING",
                "CT-PRODUCTION-SUBMISSION-GATE-CLOSED"
            ]);
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
