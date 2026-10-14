using System.Text.RegularExpressions;

namespace TradeControl.Tax.UK.Adapters.Submission.Configuration;

/// <summary>
/// Resolves the official CT test credentials from the two text files supplied by SDST.
/// The source paths and resolved values are never included in diagnostics.
/// </summary>
public sealed partial class HmrcCorporationTaxTestCredentialProvider : ISecretProvider
{
    private readonly string _registrationEmailPath;
    private readonly string _passwordsPath;
    private readonly int _passwordEntry;

    public HmrcCorporationTaxTestCredentialProvider(
        string registrationEmailPath,
        string passwordsPath,
        int passwordEntry = 1)
    {
        if (string.IsNullOrWhiteSpace(registrationEmailPath)
            || !Path.IsPathFullyQualified(registrationEmailPath))
            throw new ArgumentException("The registration-email path must be absolute.", nameof(registrationEmailPath));
        if (string.IsNullOrWhiteSpace(passwordsPath) || !Path.IsPathFullyQualified(passwordsPath))
            throw new ArgumentException("The test-password path must be absolute.", nameof(passwordsPath));
        if (passwordEntry is < 1 or > 99)
            throw new ArgumentOutOfRangeException(nameof(passwordEntry));
        _registrationEmailPath = Path.GetFullPath(registrationEmailPath);
        _passwordsPath = Path.GetFullPath(passwordsPath);
        _passwordEntry = passwordEntry;
    }

    public async ValueTask<ProtectedSecret> ResolveAsync(
        SecretReference reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        string value;
        if (reference.Value == CorporationTaxTransactionEngineSecretReferences.Password.Value)
        {
            var text = await File.ReadAllTextAsync(_passwordsPath, cancellationToken);
            value = Required(PasswordEntryPattern(_passwordEntry).Match(text), "password");
        }
        else
        {
            var text = await File.ReadAllTextAsync(_registrationEmailPath, cancellationToken);
            value = reference.Value switch
            {
                var key when key == CorporationTaxTransactionEngineSecretReferences.VendorId.Value =>
                    Required(VendorIdPattern().Match(text), "Vendor ID"),
                var key when key == CorporationTaxTransactionEngineSecretReferences.SenderId.Value =>
                    Required(SenderIdPattern().Match(text), "Sender ID"),
                var key when key == CorporationTaxTransactionEngineSecretReferences.Utr.Value =>
                    Required(UtrPattern().Match(text), "test UTR"),
                _ => throw new KeyNotFoundException("The requested secret reference is not approved by this provider.")
            };
        }
        return new ProtectedSecret(value);
    }

    private static string Required(Match match, string description)
    {
        var value = match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException($"The approved {description} is absent from the protected SDST source.");
        return value;
    }

    private static Regex PasswordEntryPattern(int entry) => new(
        $@"(?m)^\s*\[{entry}\]\s+(?<value>\S+)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"(?im)organisation.s\s+Vendor\s+ID\s+is\s+(?<value>\d{4})\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex VendorIdPattern();

    [GeneratedRegex(@"(?im)<SenderID>\s*(?<value>[^<\r\n]+)\s*</?SenderID>",
        RegexOptions.CultureInvariant)]
    private static partial Regex SenderIdPattern();

    [GeneratedRegex(@"(?im)<Key\s+Type=[""']UTR[""']>\s*(?<value>\d{10})\s*</Key>",
        RegexOptions.CultureInvariant)]
    private static partial Regex UtrPattern();
}
