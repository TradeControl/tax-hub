using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Text;

namespace TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;

internal sealed class FraudContextSnapshot
{
    public required string TenantReference { get; init; }
    public required string PrincipalReference { get; init; }
    public required string ActorReference { get; init; }
    public required string TopologyName { get; init; }
    public required string TopologyFingerprint { get; init; }
    public required DateTimeOffset CapturedAtUtc { get; init; }
    public required string JavascriptUserAgent { get; init; }
    public required Guid DeviceId { get; init; }
    public required List<FraudMultiFactorEvent> MultiFactorEvents { get; init; }
    public required string ClientPublicIp { get; init; }
    public required int ClientPublicPort { get; init; }
    public required List<FraudScreen> Screens { get; init; }
    public required string Timezone { get; init; }
    public required SortedDictionary<string, string> UserIds { get; init; }
    public required FraudWindowSize WindowSize { get; init; }
    public required List<SnapshotHop> ForwardedHops { get; init; }
    public required SortedDictionary<string, string> LicenseIds { get; init; }
    public required string ProductName { get; init; }
    public required string VendorPublicIp { get; init; }
    public required SortedDictionary<string, string> Versions { get; init; }
}

internal sealed record SnapshotHop(string By, string For);

internal static class FraudIngressCapture
{
    public static FraudContextSnapshot Capture(FraudActorIdentity identity, BrowserFraudFacts browser,
        TrustedIngressConnectionObservation observation, FraudDeploymentTopology topology,
        FraudVendorConfiguration vendor, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(browser);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(vendor);
        identity = identity.Validate();
        if (nowUtc.Offset != TimeSpan.Zero || observation.CapturedAtUtc.Offset != TimeSpan.Zero
            || observation.CapturedAtUtc < nowUtc - TimeSpan.FromMinutes(1)
            || observation.CapturedAtUtc > nowUtc + TimeSpan.FromMinutes(1))
            throw new FraudContextRejectedException("The trusted ingress capture time is outside the accepted window.");
        ValidatePort(observation.RemotePort);

        IPAddress clientAddress;
        int clientPort;
        if (observation.ForwardedClient is null)
        {
            if (topology.AcceptsForwardedClient)
                throw new FraudContextRejectedException("The configured trusted proxy did not supply the client endpoint.");
            clientAddress = observation.RemoteAddress;
            clientPort = observation.RemotePort;
        }
        else
        {
            if (!topology.AcceptsForwardedClient
                || !topology.TrustedImmediatePeers.Contains(observation.RemoteAddress))
                throw new FraudContextRejectedException("Forwarded client data came from an untrusted network peer.");
            clientAddress = observation.ForwardedClient.Address;
            clientPort = observation.ForwardedClient.Port;
            ValidatePort(clientPort);
        }
        if (!topology.AllowsNonPublicDiagnosticAddresses && !FraudValidation.IsPublic(clientAddress))
            throw new FraudContextRejectedException("The originating client endpoint is not a public address.");
        if (clientPort is 80 or 443)
            throw new FraudContextRejectedException("The originating client port is a server port.");

        foreach (var factor in browser.MultiFactorEvents)
            if (factor.TimestampUtc > observation.CapturedAtUtc + TimeSpan.FromMinutes(1))
                throw new FraudContextRejectedException("An MFA timestamp is later than the ingress capture.");

        var hops = new List<SnapshotHop>(topology.PublicTlsHops.Count);
        var sender = clientAddress;
        foreach (var receiver in topology.PublicTlsHops)
        {
            hops.Add(new(receiver.ToString(), sender.ToString()));
            sender = receiver;
        }

        return new FraudContextSnapshot
        {
            TenantReference = identity.TenantReference,
            PrincipalReference = identity.PrincipalReference,
            ActorReference = identity.ActorReference,
            TopologyName = topology.Name,
            TopologyFingerprint = topology.Fingerprint,
            CapturedAtUtc = observation.CapturedAtUtc,
            JavascriptUserAgent = browser.JavascriptUserAgent,
            DeviceId = browser.DeviceId,
            MultiFactorEvents = browser.MultiFactorEvents.ToList(),
            ClientPublicIp = clientAddress.ToString(),
            ClientPublicPort = clientPort,
            Screens = browser.Screens.ToList(),
            Timezone = browser.Timezone,
            UserIds = new(browser.UserIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            WindowSize = browser.WindowSize,
            ForwardedHops = hops,
            LicenseIds = new(vendor.LicenseIds.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                StringComparer.Ordinal),
            ProductName = vendor.ProductName,
            VendorPublicIp = topology.PublicTlsHops[0].ToString(),
            Versions = new(vendor.Versions.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal),
                StringComparer.Ordinal)
        };
    }

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new FraudContextRejectedException("The network port is invalid.");
    }
}

public sealed class FraudPreventionHeaders : IReadOnlyDictionary<string, string>
{
    private readonly IReadOnlyDictionary<string, string> _values;
    internal FraudPreventionHeaders(IDictionary<string, string> values) =>
        _values = new ReadOnlyDictionary<string, string>(new SortedDictionary<string, string>(values, StringComparer.Ordinal));
    public string this[string key] => _values[key];
    public IEnumerable<string> Keys => _values.Keys;
    public IEnumerable<string> Values => _values.Values;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out string value) => _values.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    public override string ToString() => $"FraudPreventionHeaders {{ Count = {Count}, Values = [REDACTED] }}";
}

internal static class FraudHeaderFormatter
{
    public const string SpecificationVersion = "3.3";

    public static FraudPreventionHeaders Format(FraudContextSnapshot context,
        bool allowMissingSandboxDiagnosticFacts = false)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Gov-Client-Connection-Method"] = "WEB_APP_VIA_SERVER",
            ["Gov-Client-Browser-JS-User-Agent"] = EncodeNonAscii(context.JavascriptUserAgent),
            ["Gov-Client-Device-ID"] = context.DeviceId.ToString("D"),
            ["Gov-Client-Multi-Factor"] = string.Join(',', context.MultiFactorEvents.Select(FormatFactor)),
            ["Gov-Client-Public-IP"] = context.ClientPublicIp,
            ["Gov-Client-Public-IP-Timestamp"] = context.CapturedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["Gov-Client-Public-Port"] = context.ClientPublicPort.ToString(CultureInfo.InvariantCulture),
            ["Gov-Client-Screens"] = string.Join(',', context.Screens.Select(FormatScreen)),
            ["Gov-Client-Timezone"] = context.Timezone,
            ["Gov-Client-User-IDs"] = FormatMap(context.UserIds),
            ["Gov-Client-Window-Size"] = $"width={context.WindowSize.Width}&height={context.WindowSize.Height}",
            ["Gov-Vendor-Forwarded"] = string.Join(',', context.ForwardedHops.Select(hop =>
                $"by={Encode(hop.By)}&for={Encode(hop.For)}")),
            ["Gov-Vendor-License-IDs"] = FormatMap(context.LicenseIds),
            ["Gov-Vendor-Product-Name"] = Encode(context.ProductName),
            ["Gov-Vendor-Public-IP"] = context.VendorPublicIp,
            ["Gov-Vendor-Version"] = FormatMap(context.Versions)
        };
        var permittedEmpty = allowMissingSandboxDiagnosticFacts
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Gov-Client-Multi-Factor", "Gov-Client-User-IDs", "Gov-Vendor-License-IDs"
            }
            : [];
        if (headers.Any(header => (string.IsNullOrWhiteSpace(header.Value) && !permittedEmpty.Contains(header.Key))
            || header.Value.Any(character => character > 127 || char.IsControl(character))))
            throw new FraudContextRejectedException("A fraud-prevention header is empty or not US-ASCII.");
        return new(headers);
    }

    private static string FormatFactor(FraudMultiFactorEvent factor) =>
        $"type={FactorName(factor.Type)}&timestamp={Encode(factor.TimestampUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))}&unique-reference={Encode(factor.UniqueReference)}";

    private static string FactorName(FraudMultiFactorType type) => type switch
    {
        FraudMultiFactorType.Totp => "TOTP",
        FraudMultiFactorType.AuthorisationCode => "AUTH_CODE",
        FraudMultiFactorType.Other => "OTHER",
        _ => throw new FraudContextRejectedException("The MFA type is unsupported.")
    };

    private static string FormatScreen(FraudScreen screen) =>
        $"width={screen.Width}&height={screen.Height}&scaling-factor={screen.ScalingFactor.ToString(CultureInfo.InvariantCulture)}&colour-depth={screen.ColourDepth}";

    private static string FormatMap(IReadOnlyDictionary<string, string> values) => string.Join('&',
        values.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{Encode(item.Key)}={Encode(item.Value)}"));

    private static string Encode(string value) => Uri.EscapeDataString(value);

    private static string EncodeNonAscii(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii) builder.Append((char)rune.Value);
            else builder.Append(Uri.EscapeDataString(rune.ToString()));
        }
        return builder.ToString();
    }
}

public sealed class FraudContextRejectedException : InvalidOperationException
{
    public FraudContextRejectedException(string message) : base(message) { }
}
