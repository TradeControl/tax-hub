using TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Company;

public interface ICompaniesHouseOfficialTestPreflight
{
    bool Enabled { get; }
    Task<CompaniesHouseOfficialTestPreflightOutcome> ExecuteAsync(
        CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default);
}

public sealed record CompaniesHouseProtectedConfigurationStatus(
    bool TestPresenterConfigurationAvailable,
    bool CompanyAuthenticationAvailable);

public sealed record CompaniesHousePreflightDigests(
    string PreviewSha256,
    string AccountsSha256,
    string? WireSha256);

public sealed record CompaniesHouseOfficialTestPreflightOutcome(
    string EvidenceClass,
    string SubmissionNumber,
    string TransactionId,
    string PreparationSource,
    string IntendedEndpoint,
    CompaniesHouseProtectedConfigurationStatus ProtectedConfiguration,
    CompaniesHousePreflightDigests Digests,
    bool DatabasePackagePrepared,
    bool CredentialedEnvelopeMaterialized,
    bool ExternalSendEnabled,
    IReadOnlyList<string> BlockingReasons);

public sealed class CompaniesHouseOfficialTestPreflight(
    ICompaniesHouseAccountsRunner accountsRunner,
    ISecretProvider testAccountSecrets,
    ISecretProvider companyAuthenticationSecrets) : ICompaniesHouseOfficialTestPreflight
{
    public const string EvidenceClass = "OFFICIAL TEST PREFLIGHT — NOT SENT";

    public bool Enabled => true;

    public async Task<CompaniesHouseOfficialTestPreflightOutcome> ExecuteAsync(
        CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var transactionId = payload.Filing.TransactionId;
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > 32
            || transactionId.Any(value => !char.IsAsciiLetterOrDigit(value)))
            throw new ArgumentException("A bounded alphanumeric filing.transactionId is required.", nameof(payload));

        var package = await accountsRunner.PrepareAsync(payload, cancellationToken);
        var testAccountAvailable = await HasTestAccountConfigurationAsync(cancellationToken);
        var companyAuthenticationAvailable = await CanResolveAsync(companyAuthenticationSecrets,
            CompaniesHouseSecretReferences.CompanyAuthentication, cancellationToken);
        var blockers = new List<string>();
        if (!testAccountAvailable) blockers.Add("CH-TEST-ACCOUNT-CONFIGURATION-NOT-AVAILABLE");
        if (!companyAuthenticationAvailable) blockers.Add("CH-COMPANY-AUTHENTICATION-NOT-CONFIGURED");

        CompaniesHouseMaterializedEnvelope? materialized = null;
        if (testAccountAvailable && companyAuthenticationAvailable)
        {
            var routed = new RoutedSecretProvider(new Dictionary<SecretReference, ISecretProvider>
            {
                [CompaniesHouseSecretReferences.TestPresenterId] = testAccountSecrets,
                [CompaniesHouseSecretReferences.TestPresenterAuthentication] = testAccountSecrets,
                [CompaniesHouseSecretReferences.TestFlag] = testAccountSecrets,
                [CompaniesHouseSecretReferences.TestPackageReference] = testAccountSecrets,
                [CompaniesHouseSecretReferences.CompanyAuthentication] = companyAuthenticationSecrets
            });
            materialized = await new CompaniesHouseEnvelopeMaterializer(routed)
                .MaterializeTestAsync(package, transactionId, cancellationToken);
        }
        blockers.Add("CH-EXTERNAL-SEND-DISABLED");
        blockers.Add("CH-DEVELOPER-TEST-EVIDENCE-PENDING");
        return new(EvidenceClass, payload.Filing.SubmissionNumber, transactionId,
            "Trade Control database → statutory accounts → TIS 6.0 FormSubmission preview",
            CompaniesHouseGatewayEndpoints.OfficialTest.ToString(),
            new(testAccountAvailable, companyAuthenticationAvailable),
            new(package.Transmission.Sha256, package.Documents.Single().Artifact.Sha256,
                materialized?.WireSha256),
            true, materialized is not null, false, blockers);
    }

    private async Task<bool> HasTestAccountConfigurationAsync(CancellationToken cancellationToken)
    {
        foreach (var reference in new[]
                 {
                     CompaniesHouseSecretReferences.TestPresenterId,
                     CompaniesHouseSecretReferences.TestPresenterAuthentication,
                     CompaniesHouseSecretReferences.TestFlag,
                     CompaniesHouseSecretReferences.TestPackageReference
                 })
            if (!await CanResolveAsync(testAccountSecrets, reference, cancellationToken)) return false;
        return true;
    }

    private static async Task<bool> CanResolveAsync(ISecretProvider provider, SecretReference reference,
        CancellationToken cancellationToken)
    {
        try
        {
            using var value = await provider.ResolveAsync(reference, cancellationToken);
            return value.Length != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                          or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }
}

public sealed class DisabledCompaniesHouseOfficialTestPreflight : ICompaniesHouseOfficialTestPreflight
{
    public bool Enabled => false;

    public Task<CompaniesHouseOfficialTestPreflightOutcome> ExecuteAsync(
        CompaniesHouseAccountsPayload payload,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Companies House official-test preflight is available only in Development.");
}
