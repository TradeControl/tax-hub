using System.Collections.Immutable;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

public sealed record CorporationTaxOfficialTestMaterialization(
    ImmutableArray<byte> Content,
    string TransactionId,
    string ServiceArtifactSha256,
    string WireSha256,
    string Irmark,
    ImmutableArray<string> AttachmentSha256,
    string MessageClass,
    string IntendedEndpoint,
    string EvidenceClass)
{
    public const string MaterializedEvidence = "HMRC ETS MATERIALISED — NOT SENT";

    public override string ToString() =>
        $"{EvidenceClass}:{ServiceArtifactSha256}:{WireSha256}";
}

/// <summary>
/// Adds protected SDST test identity and the IRmark to an immutable Objective 3 CT package.
/// This component owns no HTTP client and cannot send the resulting request.
/// </summary>
public sealed class CorporationTaxOfficialTestMaterializer(ISecretProvider secrets)
{
    public async Task<CorporationTaxOfficialTestMaterialization> MaterializeAsync(
        PreparedSubmissionPackage package,
        string transactionId,
        string product,
        string productVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        using var vendorId = await secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.VendorId, cancellationToken);
        using var senderId = await secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.SenderId, cancellationToken);
        using var password = await secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.Password, cancellationToken);
        using var utr = await secrets.ResolveAsync(
            CorporationTaxTransactionEngineSecretReferences.Utr, cancellationToken);

        var identity = new TransactionEngineSubmissionIdentity(
            Copy(vendorId), Copy(senderId), Copy(password), Copy(utr), product, productVersion);
        var options = TransactionEngineProtocolOptions.CorporationTaxExternalTest();
        var result = TransactionEngineWire.Materialize(package, identity, transactionId, options);
        return new(result.Content, result.TransactionId, result.ServiceRootSha256, result.WireSha256,
            result.Irmark, result.AttachmentSha256, options.MessageClass,
            options.SubmissionEndpoint.AbsoluteUri,
            CorporationTaxOfficialTestMaterialization.MaterializedEvidence);
    }

    private static string Copy(ProtectedSecret secret)
    {
        var characters = new char[secret.Length];
        try
        {
            secret.CopyTo(characters);
            return new string(characters);
        }
        finally
        {
            Array.Clear(characters);
        }
    }
}
