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
