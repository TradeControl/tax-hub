namespace TradeControl.Tax.UK.Adapters.Submission.Configuration;

public enum AuthorityEnvironment
{
    Sandbox,
    Production
}

public sealed record HmrcEnvironmentProfile(
    AuthorityEnvironment Environment,
    Uri ApiBaseUri,
    Uri AuthorisationBaseUri,
    bool LiveRequestsEnabled);

public static class HmrcEnvironmentProfiles
{
    public static HmrcEnvironmentProfile Sandbox { get; } = new(
        AuthorityEnvironment.Sandbox,
        new("https://test-api.service.hmrc.gov.uk/", UriKind.Absolute),
        new("https://test-www.tax.service.gov.uk/", UriKind.Absolute),
        true);

    public static HmrcEnvironmentProfile Production { get; } = new(
        AuthorityEnvironment.Production,
        new("https://api.service.hmrc.gov.uk/", UriKind.Absolute),
        new("https://www.tax.service.gov.uk/", UriKind.Absolute),
        false);

    private static readonly HashSet<string> AllowedHosts =
    [
        Sandbox.ApiBaseUri.Host,
        Sandbox.AuthorisationBaseUri.Host,
        Production.ApiBaseUri.Host,
        Production.AuthorisationBaseUri.Host
    ];

    public static bool IsAllowedHost(Uri uri) => uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && AllowedHosts.Contains(uri.IdnHost);

    public static Uri ResolveRelative(HmrcEnvironmentProfile profile, string relativePath)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(relativePath)
            || !relativePath.StartsWith('/')
            || relativePath.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("Only an authority-relative path can be resolved.", nameof(relativePath));
        var resolved = new Uri(profile.ApiBaseUri, relativePath);
        if (!IsAllowedHost(resolved) || resolved.Host != profile.ApiBaseUri.Host)
            throw new InvalidOperationException("The resolved HMRC host is not allowed for this profile.");
        return resolved;
    }
}
