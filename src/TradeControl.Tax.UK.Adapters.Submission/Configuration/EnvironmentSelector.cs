namespace TradeControl.Tax.UK.Adapters.Submission.Configuration;

public sealed class EnvironmentSelector
{
    private readonly HmrcEnvironmentProfile _selected;

    private EnvironmentSelector(HmrcEnvironmentProfile selected)
    {
        if (!HmrcEnvironmentProfiles.IsAllowedHost(selected.ApiBaseUri)
            || !HmrcEnvironmentProfiles.IsAllowedHost(selected.AuthorisationBaseUri))
            throw new ArgumentException("The configured HMRC profile contains an unapproved host.", nameof(selected));
        _selected = selected;
    }

    public static EnvironmentSelector Sandbox() => new(HmrcEnvironmentProfiles.Sandbox);

    // Production remains deliberately unavailable to request or prepared-artifact input.
    // A later reviewed host-composition change must explicitly enable this internal path.
    internal static EnvironmentSelector Production() => new(HmrcEnvironmentProfiles.Production);

    public HmrcEnvironmentProfile Selected => _selected;

    public Uri ResolveApiPath(string relativePath) =>
        HmrcEnvironmentProfiles.ResolveRelative(_selected, relativePath);
}
