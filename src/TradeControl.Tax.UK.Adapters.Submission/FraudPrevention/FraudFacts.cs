using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;

public enum FraudMultiFactorType { Totp, AuthorisationCode, Other }

public sealed record FraudMultiFactorEvent(
    FraudMultiFactorType Type, DateTimeOffset TimestampUtc, string UniqueReference);

public sealed record FraudScreen(int Width, int Height, decimal ScalingFactor, int ColourDepth);
public sealed record FraudWindowSize(int Width, int Height);

public sealed class BrowserFraudFacts
{
    public BrowserFraudFacts(string javascriptUserAgent, Guid deviceId,
        IReadOnlyList<FraudMultiFactorEvent> multiFactorEvents, IReadOnlyList<FraudScreen> screens,
        string timezone, IReadOnlyDictionary<string, string> userIds, FraudWindowSize windowSize)
    {
        JavascriptUserAgent = FraudValidation.Text(javascriptUserAgent, 1024, nameof(javascriptUserAgent));
        if (deviceId == Guid.Empty) throw new ArgumentException("A persistent device identifier is required.", nameof(deviceId));
        DeviceId = deviceId;
        MultiFactorEvents = FraudValidation.Items(multiFactorEvents, 0, 8, nameof(multiFactorEvents));
        Screens = FraudValidation.Items(screens, 1, 8, nameof(screens));
        Timezone = FraudValidation.Timezone(timezone);
        UserIds = FraudValidation.Map(userIds, 0, 8, 128, nameof(userIds));
        WindowSize = windowSize ?? throw new ArgumentNullException(nameof(windowSize));

        foreach (var factor in MultiFactorEvents)
        {
            if (factor.TimestampUtc.Offset != TimeSpan.Zero)
                throw new ArgumentException("MFA timestamps must use UTC.", nameof(multiFactorEvents));
            FraudValidation.Sha256Hex(factor.UniqueReference, nameof(multiFactorEvents));
        }
        foreach (var screen in Screens)
        {
            FraudValidation.Dimension(screen.Width, nameof(screens));
            FraudValidation.Dimension(screen.Height, nameof(screens));
            if (screen.ScalingFactor <= 0 || screen.ScalingFactor > 16)
                throw new ArgumentException("Screen scaling is outside the accepted range.", nameof(screens));
            if (screen.ColourDepth is < 1 or > 128)
                throw new ArgumentException("Screen colour depth is outside the accepted range.", nameof(screens));
        }
        FraudValidation.Dimension(WindowSize.Width, nameof(windowSize));
        FraudValidation.Dimension(WindowSize.Height, nameof(windowSize));
    }

    public string JavascriptUserAgent { get; }
    public Guid DeviceId { get; }
    public IReadOnlyList<FraudMultiFactorEvent> MultiFactorEvents { get; }
    public IReadOnlyList<FraudScreen> Screens { get; }
    public string Timezone { get; }
    public IReadOnlyDictionary<string, string> UserIds { get; }
    public FraudWindowSize WindowSize { get; }
    public override string ToString() => "BrowserFraudFacts { [REDACTED] }";
}

public sealed class FraudVendorConfiguration
{
    public FraudVendorConfiguration(string productName, IReadOnlyDictionary<string, string> versions,
        IReadOnlyDictionary<string, string> licenseIds)
    {
        ProductName = FraudValidation.Text(productName, 128, nameof(productName));
        Versions = FraudValidation.Map(versions, 1, 16, 64, nameof(versions));
        LicenseIds = FraudValidation.Map(licenseIds, 0, 16, 128, nameof(licenseIds));
        foreach (var license in LicenseIds.Values) FraudValidation.Sha256Hex(license, nameof(licenseIds));
    }

    public string ProductName { get; }
    public IReadOnlyDictionary<string, string> Versions { get; }
    public IReadOnlyDictionary<string, string> LicenseIds { get; }
    public override string ToString() => "FraudVendorConfiguration { [REDACTED] }";
}

public sealed record ForwardedClientEndpoint(IPAddress Address, int Port);

// Host composition may create this only from the accepted socket/proxy boundary, never request JSON.
public sealed record TrustedIngressConnectionObservation(
    IPAddress RemoteAddress,
    int RemotePort,
    DateTimeOffset CapturedAtUtc,
    ForwardedClientEndpoint? ForwardedClient = null);

public sealed record FraudForwardedHop(IPAddress By, IPAddress For);

public sealed class FraudDeploymentTopology
{
    private FraudDeploymentTopology(string name, bool acceptsForwardedClient,
        IReadOnlySet<IPAddress> trustedImmediatePeers, IReadOnlyList<IPAddress> publicTlsHops,
        bool allowsNonPublicDiagnosticAddresses = false)
    {
        Name = FraudValidation.Text(name, 64, nameof(name));
        AcceptsForwardedClient = acceptsForwardedClient;
        TrustedImmediatePeers = new HashSet<IPAddress>(trustedImmediatePeers ?? throw new ArgumentNullException(nameof(trustedImmediatePeers)));
        PublicTlsHops = FraudValidation.Items(publicTlsHops, 1, 16, nameof(publicTlsHops));
        if (!allowsNonPublicDiagnosticAddresses && PublicTlsHops.Any(address => !FraudValidation.IsPublic(address)))
            throw new ArgumentException("Every configured internet TLS hop must have a public address.", nameof(publicTlsHops));
        if (acceptsForwardedClient && TrustedImmediatePeers.Count == 0)
            throw new ArgumentException("A forwarded topology requires an exact trusted immediate-peer allow-list.", nameof(trustedImmediatePeers));
        if (!acceptsForwardedClient && TrustedImmediatePeers.Count != 0)
            throw new ArgumentException("A direct topology cannot contain trusted proxy peers.", nameof(trustedImmediatePeers));
        AllowsNonPublicDiagnosticAddresses = allowsNonPublicDiagnosticAddresses;
        var fingerprintInput = string.Join('|', new[] { Name, AcceptsForwardedClient.ToString(),
                AllowsNonPublicDiagnosticAddresses.ToString() }
            .Concat(TrustedImmediatePeers.Select(address => address.ToString()).OrderBy(value => value, StringComparer.Ordinal))
            .Concat(PublicTlsHops.Select(address => address.ToString())));
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput)));
    }

    public string Name { get; }
    public bool AcceptsForwardedClient { get; }
    public IReadOnlySet<IPAddress> TrustedImmediatePeers { get; }
    public IReadOnlyList<IPAddress> PublicTlsHops { get; }
    internal bool AllowsNonPublicDiagnosticAddresses { get; }
    internal string Fingerprint { get; }

    public static FraudDeploymentTopology Direct(string name, IPAddress publicServerAddress) =>
        new(name, false, new HashSet<IPAddress>(), [publicServerAddress]);

    public static FraudDeploymentTopology TrustedProxyChain(string name,
        IReadOnlySet<IPAddress> trustedImmediatePeers, IReadOnlyList<IPAddress> publicTlsHops) =>
        new(name, true, trustedImmediatePeers, publicTlsHops);

    // Validator-only: preserves the actual local socket facts so HMRC can diagnose them.
    public static FraudDeploymentTopology SandboxValidatorDiagnosticDirect(string name,
        IPAddress observedServerAddress) =>
        new(name, false, new HashSet<IPAddress>(), [observedServerAddress],
            allowsNonPublicDiagnosticAddresses: true);

    public override string ToString() => $"FraudDeploymentTopology {{ Name = {Name}, Addresses = [REDACTED] }}";
}

public sealed record FraudActorIdentity(string TenantReference, string PrincipalReference, string ActorReference)
{
    public FraudActorIdentity Validate() => new(
        FraudValidation.Reference(TenantReference, nameof(TenantReference)),
        FraudValidation.Reference(PrincipalReference, nameof(PrincipalReference)),
        FraudValidation.Reference(ActorReference, nameof(ActorReference)));
    public override string ToString() => "FraudActorIdentity { [REDACTED] }";
}

public sealed record SealedFraudContextReference
{
    public SealedFraudContextReference(string value)
    {
        if (!Regex.IsMatch(value ?? string.Empty, "^[A-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A sealed fraud-context reference is invalid.", nameof(value));
        Value = value!;
    }
    public string Value { get; }
    public override string ToString() => "[SEALED-FRAUD-CONTEXT]";
}

internal static class FraudValidation
{
    private static readonly Regex TimezonePattern = new("^UTC[+-](?<hour>\\d{2}):(?<minute>\\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Text(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
            throw new ArgumentException("A bounded control-free fraud fact is required.", name);
        return value!;
    }

    public static string Reference(string value, string name) => Text(value?.Trim() ?? string.Empty, 256, name);

    public static IReadOnlyList<T> Items<T>(IReadOnlyList<T> values, int minimum, int maximum, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Count < minimum || values.Count > maximum || values.Any(item => item is null))
            throw new ArgumentException("The fraud-fact collection has an invalid size or item.", name);
        return new ReadOnlyCollection<T>(values.ToArray());
    }

    public static IReadOnlyDictionary<string, string> Map(IReadOnlyDictionary<string, string> values,
        int minimum, int maximum, int valueMaximum, string name)
    {
        ArgumentNullException.ThrowIfNull(values, name);
        if (values.Count < minimum || values.Count > maximum)
            throw new ArgumentException("The fraud-fact map has an invalid size.", name);
        var copy = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
            copy.Add(Text(item.Key, 64, name), Text(item.Value, valueMaximum, name));
        return new ReadOnlyDictionary<string, string>(copy);
    }

    public static string Timezone(string value)
    {
        var match = TimezonePattern.Match(value ?? string.Empty);
        if (!match.Success || int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture) > 14
            || int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) > 59)
            throw new ArgumentException("The browser timezone must use a valid UTC offset.", nameof(value));
        return value!;
    }

    public static void Sha256Hex(string value, string name)
    {
        if (!Regex.IsMatch(value ?? string.Empty, "^[A-Fa-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A SHA-256 hexadecimal reference is required.", name);
    }

    public static void Dimension(int value, string name)
    {
        if (value is < 1 or > 100_000) throw new ArgumentException("A positive bounded dimension is required.", name);
    }

    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] != 0 && bytes[0] != 10 && bytes[0] != 127
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2)
                && !(bytes[0] == 198 && bytes[1] is 18 or 19)
                && !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100)
                && !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113)
                && bytes[0] < 224;
        return (bytes[0] & 0xfe) != 0xfc
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8);
    }
}
