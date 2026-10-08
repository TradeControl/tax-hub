using TradeControl.Tax.UK.Adapters.Submission.Configuration;

namespace TradeControl.Tax.UK.Adapters.Submission.CompaniesHouse;

/// <summary>
/// Server-derived scope used to resolve company authentication only at dispatch time. The SHA-256
/// identity prevents the Submission adapter from requiring a browser-supplied company number.
/// </summary>
public sealed record CompaniesHouseCompanyAuthenticationScope
{
    public CompaniesHouseCompanyAuthenticationScope(string tenantReference, string companyIdentitySha256)
    {
        if (!Guid.TryParse(tenantReference, out _))
            throw new ArgumentException("An opaque tenant GUID is required.", nameof(tenantReference));
        if (string.IsNullOrWhiteSpace(companyIdentitySha256) || companyIdentitySha256.Length != 64
            || companyIdentitySha256.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("A company-identity SHA-256 value is required.",
                nameof(companyIdentitySha256));
        TenantReference = tenantReference;
        CompanyIdentitySha256 = companyIdentitySha256.ToUpperInvariant();
    }

    public string TenantReference { get; }
    public string CompanyIdentitySha256 { get; }
}

/// <summary>
/// Protected host boundary for company-specific authentication. Implementations return a
/// short-lived protected value and must not persist or log its clear text.
/// </summary>
public interface ICompaniesHouseCompanyAuthenticationResolver
{
    ValueTask<ProtectedSecret> ResolveAsync(CompaniesHouseCompanyAuthenticationScope scope,
        CancellationToken cancellationToken = default);
}
