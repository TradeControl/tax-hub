using System.Security.Cryptography;
using System.Text.Json;

namespace TradeControl.Tax.UK.Adapters.Submission.Configuration;

public sealed record SecretReference
{
    public SecretReference(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            throw new ArgumentException("A bounded secret reference is required.", nameof(value));
        Value = value;
    }

    public string Value { get; }
    public override string ToString() => "[SECRET-REFERENCE]";
}

public sealed class ProtectedSecret : IDisposable
{
    private char[]? _value;

    internal ProtectedSecret(string value)
    {
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("The resolved secret is empty.");
        _value = value.ToCharArray();
    }

    public int Length => _value?.Length ?? throw new ObjectDisposedException(nameof(ProtectedSecret));

    public void CopyTo(Span<char> destination)
    {
        var value = _value ?? throw new ObjectDisposedException(nameof(ProtectedSecret));
        if (destination.Length < value.Length)
            throw new ArgumentException("The destination is too small.", nameof(destination));
        value.CopyTo(destination);
    }

    public override string ToString() => "[PROTECTED]";

    public void Dispose()
    {
        if (_value is null) return;
        CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(_value.AsSpan()));
        _value = null;
    }
}

public interface ISecretProvider
{
    ValueTask<ProtectedSecret> ResolveAsync(SecretReference reference,
        CancellationToken cancellationToken = default);
}

public sealed class EnvironmentSecretProvider : ISecretProvider
{
    private readonly IReadOnlyDictionary<string, string> _approvedVariables;

    public EnvironmentSecretProvider(IReadOnlyDictionary<SecretReference, string> approvedVariables)
    {
        ArgumentNullException.ThrowIfNull(approvedVariables);
        if (approvedVariables.Count == 0 || approvedVariables.Any(item => string.IsNullOrWhiteSpace(item.Value)))
            throw new ArgumentException("At least one approved environment secret is required.", nameof(approvedVariables));
        _approvedVariables = approvedVariables.ToDictionary(item => item.Key.Value, item => item.Value,
            StringComparer.Ordinal);
    }

    public ValueTask<ProtectedSecret> ResolveAsync(SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_approvedVariables.TryGetValue(reference.Value, out var variableName))
            throw new KeyNotFoundException("The requested secret reference is not approved by this provider.");
        var value = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("The approved secret is absent from the protected environment.");
        return ValueTask.FromResult(new ProtectedSecret(value));
    }
}

public sealed class JsonFileSecretProvider : ISecretProvider
{
    private readonly string _settingsPath;
    private readonly IReadOnlyDictionary<string, string> _approvedJsonProperties;

    public JsonFileSecretProvider(string settingsPath, IReadOnlyDictionary<SecretReference, string> approvedSecrets)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !Path.IsPathFullyQualified(settingsPath))
            throw new ArgumentException("The secret settings path must be absolute.", nameof(settingsPath));
        ArgumentNullException.ThrowIfNull(approvedSecrets);
        if (approvedSecrets.Count == 0 || approvedSecrets.Any(item => string.IsNullOrWhiteSpace(item.Value)))
            throw new ArgumentException("At least one approved JSON secret property is required.", nameof(approvedSecrets));

        _settingsPath = Path.GetFullPath(settingsPath);
        _approvedJsonProperties = approvedSecrets.ToDictionary(item => item.Key.Value, item => item.Value,
            StringComparer.Ordinal);
    }

    public async ValueTask<ProtectedSecret> ResolveAsync(SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_approvedJsonProperties.TryGetValue(reference.Value, out var propertyName))
            throw new KeyNotFoundException("The requested secret reference is not approved by this provider.");

        await using var stream = new FileStream(_settingsPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty(propertyName, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrEmpty(property.GetString()))
            throw new InvalidOperationException("The approved secret is absent from the protected settings source.");
        return new ProtectedSecret(property.GetString()!);
    }
}

public sealed class LabelledTextFileSecretProvider : ISecretProvider
{
    private readonly string _settingsPath;
    private readonly IReadOnlyDictionary<string, string> _approvedLabels;

    public LabelledTextFileSecretProvider(string settingsPath,
        IReadOnlyDictionary<SecretReference, string> approvedLabels)
    {
        if (string.IsNullOrWhiteSpace(settingsPath) || !Path.IsPathFullyQualified(settingsPath))
            throw new ArgumentException("The secret settings path must be absolute.", nameof(settingsPath));
        ArgumentNullException.ThrowIfNull(approvedLabels);
        if (approvedLabels.Count == 0 || approvedLabels.Any(item => string.IsNullOrWhiteSpace(item.Value)))
            throw new ArgumentException("At least one approved text label is required.", nameof(approvedLabels));
        _settingsPath = Path.GetFullPath(settingsPath);
        _approvedLabels = approvedLabels.ToDictionary(item => item.Key.Value, item => item.Value,
            StringComparer.Ordinal);
    }

    public async ValueTask<ProtectedSecret> ResolveAsync(SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_approvedLabels.TryGetValue(reference.Value, out var approvedLabel))
            throw new KeyNotFoundException("The requested secret reference is not approved by this provider.");

        string? resolved = null;
        using var reader = new StreamReader(new FileStream(_settingsPath, FileMode.Open, FileAccess.Read,
            FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan));
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            var delimiter = line.IndexOfAny([':', '=']);
            if (delimiter < 0 || !string.Equals(line[..delimiter].Trim(), approvedLabel, StringComparison.Ordinal))
                continue;
            if (resolved is not null)
                throw new InvalidOperationException("The approved secret label occurs more than once.");
            resolved = line[(delimiter + 1)..].Trim();
        }
        if (string.IsNullOrEmpty(resolved))
            throw new InvalidOperationException("The approved secret is absent from the protected settings source.");
        return new ProtectedSecret(resolved);
    }
}

public sealed class RoutedSecretProvider : ISecretProvider
{
    private readonly IReadOnlyDictionary<string, ISecretProvider> _routes;

    public RoutedSecretProvider(IReadOnlyDictionary<SecretReference, ISecretProvider> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        if (routes.Count == 0 || routes.Any(item => item.Value is null))
            throw new ArgumentException("At least one approved secret-provider route is required.", nameof(routes));
        _routes = routes.ToDictionary(item => item.Key.Value, item => item.Value, StringComparer.Ordinal);
    }

    public ValueTask<ProtectedSecret> ResolveAsync(SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        if (!_routes.TryGetValue(reference.Value, out var provider))
            throw new KeyNotFoundException("The requested secret reference has no approved provider route.");
        return provider.ResolveAsync(reference, cancellationToken);
    }
}

public static class HmrcSecretReferences
{
    public static SecretReference ClientId { get; } = new("hmrc-api-client-id");
    public static SecretReference ClientSecret { get; } = new("hmrc-api-client-secret");

    public static IReadOnlyDictionary<SecretReference, string> LegacyVatSandboxJsonProperties { get; } =
        new Dictionary<SecretReference, string>
        {
            [ClientId] = "clientId",
            [ClientSecret] = "clientSecret"
        };

    public static IReadOnlyDictionary<SecretReference, string> AppServiceEnvironmentVariables { get; } =
        new Dictionary<SecretReference, string>
        {
            [ClientId] = "TaxHub__HmrcSandbox__ClientId",
            [ClientSecret] = "TaxHub__HmrcSandbox__ClientSecret"
        };
}

public static class CompaniesHouseSecretReferences
{
    public static SecretReference TestPresenterId { get; } = new("companies-house-test-presenter-id");
    public static SecretReference TestPresenterAuthentication { get; } =
        new("companies-house-test-presenter-authentication");
    public static SecretReference TestFlag { get; } = new("companies-house-test-flag");
    public static SecretReference TestPackageReference { get; } =
        new("companies-house-test-package-reference");
    public static SecretReference CompanyAuthentication { get; } =
        new("companies-house-company-authentication");
    public static SecretReference LivePresenterId { get; } = new("companies-house-live-presenter-id");
    public static SecretReference LivePresenterAuthentication { get; } =
        new("companies-house-live-presenter-authentication");
    public static SecretReference LivePackageReference { get; } =
        new("companies-house-live-package-reference");

    public static IReadOnlyDictionary<SecretReference, string> JsonProperties { get; } =
        new Dictionary<SecretReference, string>
        {
            [TestPresenterId] = "testPresenterId",
            [TestPresenterAuthentication] = "testPresenterAuthentication",
            [TestFlag] = "testFlag",
            [TestPackageReference] = "testPackageReference",
            [CompanyAuthentication] = "companyAuthentication",
            [LivePresenterId] = "livePresenterId",
            [LivePresenterAuthentication] = "livePresenterAuthentication",
            [LivePackageReference] = "livePackageReference"
        };

    public static IReadOnlyDictionary<SecretReference, string> TestAccountFileLabels { get; } =
        new Dictionary<SecretReference, string>
        {
            [TestPresenterId] = "Test Presenter ID",
            [TestPresenterAuthentication] = "Authentication Value",
            [TestFlag] = "Test Flag",
            [TestPackageReference] = "Test Package Reference"
        };

    public static IReadOnlyDictionary<SecretReference, string> AppServiceEnvironmentVariables { get; } =
        new Dictionary<SecretReference, string>
        {
            [TestPresenterId] = "TaxHub__CompaniesHouse__TestPresenterId",
            [TestPresenterAuthentication] = "TaxHub__CompaniesHouse__TestPresenterAuthentication",
            [TestFlag] = "TaxHub__CompaniesHouse__TestFlag",
            [TestPackageReference] = "TaxHub__CompaniesHouse__TestPackageReference",
            [CompanyAuthentication] = "TaxHub__CompaniesHouse__CompanyAuthentication",
            [LivePresenterId] = "TaxHub__CompaniesHouse__LivePresenterId",
            [LivePresenterAuthentication] = "TaxHub__CompaniesHouse__LivePresenterAuthentication",
            [LivePackageReference] = "TaxHub__CompaniesHouse__LivePackageReference"
        };
}

public static class CorporationTaxTransactionEngineSecretReferences
{
    public static SecretReference VendorId { get; } = new("ct-transaction-engine-vendor-id");
    public static SecretReference SenderId { get; } = new("ct-transaction-engine-test-sender-id");
    public static SecretReference Password { get; } = new("ct-transaction-engine-test-password");
    public static SecretReference Utr { get; } = new("ct-transaction-engine-test-utr");
}
