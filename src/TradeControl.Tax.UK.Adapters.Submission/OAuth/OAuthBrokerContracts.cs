using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeControl.Tax.UK.Adapters.Submission.OAuth;

public static class OAuthBrokerContract
{
    public const string HandoffPurpose = "TradeControl.HmrcOAuthBroker.Handoff.v1";
    public const string CompletionPurpose = "TradeControl.HmrcOAuthBroker.Completion.v1";
    public const string CorrelationCookie = "__Host-tc-hmrc-correlation";
    public const string CompletionPath = "/TaxHub/HmrcBrokerComplete";
    public static readonly TimeSpan MaximumHandoffLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaximumCompletionLifetime = TimeSpan.FromMinutes(2);
}

public sealed record OAuthBrokerHandoff(
    string Environment,
    string TenantReference,
    string PrincipalReference,
    string ActorReference,
    string AuthorisationUri,
    string State,
    string ReturnOrigin,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record OAuthBrokerCompletion(
    string Environment,
    string TenantReference,
    string PrincipalReference,
    string ActorReference,
    string State,
    string? Code,
    string? Error,
    string ReturnOrigin,
    DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Produces purpose-bound, authenticated and confidential browser tickets. The shared broker key is
/// deliberately independent of ASP.NET Identity and Data Protection key rings.
/// </summary>
public sealed class OAuthBrokerTicketProtector : IDisposable
{
    private const byte Version = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int MaximumTicketBytes = 32 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly byte[] _masterKey;

    public OAuthBrokerTicketProtector(ReadOnlySpan<byte> masterKey)
    {
        if (masterKey.Length != 32)
            throw new ArgumentException("The OAuth broker envelope key must contain exactly 32 bytes.",
                nameof(masterKey));
        _masterKey = masterKey.ToArray();
    }

    public static OAuthBrokerTicketProtector FromBase64(string encodedKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encodedKey);
        byte[] key;
        try { key = Convert.FromBase64String(encodedKey); }
        catch (FormatException exception)
        {
            throw new ArgumentException("The OAuth broker envelope key is not valid Base64.",
                nameof(encodedKey), exception);
        }
        try { return new OAuthBrokerTicketProtector(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public string Protect<T>(T value, string purpose)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (plaintext.Length > MaximumTicketBytes)
            throw new InvalidOperationException("The OAuth broker ticket exceeds its size bound.");
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        var purposeBytes = RequiredPurpose(purpose);
        var key = DeriveKey(purposeBytes);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, purposeBytes);
            var ticket = new byte[1 + NonceSize + TagSize + ciphertext.Length];
            ticket[0] = Version;
            nonce.CopyTo(ticket, 1);
            tag.CopyTo(ticket, 1 + NonceSize);
            ciphertext.CopyTo(ticket, 1 + NonceSize + TagSize);
            return Base64UrlEncode(ticket);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public T Unprotect<T>(string ticket, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ticket);
        var encoded = Base64UrlDecode(ticket);
        if (encoded.Length is < 1 + NonceSize + TagSize + 1 or > MaximumTicketBytes + 1 + NonceSize + TagSize
            || encoded[0] != Version)
            throw new CryptographicException("The OAuth broker ticket is invalid.");
        var purposeBytes = RequiredPurpose(purpose);
        var key = DeriveKey(purposeBytes);
        var plaintext = new byte[encoded.Length - 1 - NonceSize - TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(encoded.AsSpan(1, NonceSize),
                encoded.AsSpan(1 + NonceSize + TagSize),
                encoded.AsSpan(1 + NonceSize, TagSize), plaintext, purposeBytes);
            return JsonSerializer.Deserialize<T>(plaintext, Json)
                ?? throw new CryptographicException("The OAuth broker ticket is empty.");
        }
        catch (JsonException exception)
        {
            throw new CryptographicException("The OAuth broker ticket payload is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public static string Fingerprint(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(_masterKey);

    private byte[] DeriveKey(byte[] purpose) => HMACSHA256.HashData(_masterKey, purpose);

    private static byte[] RequiredPurpose(string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        return Encoding.UTF8.GetBytes(purpose);
    }

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        if (value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
            throw new CryptographicException("The OAuth broker ticket encoding is invalid.");
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            0 => base64,
            2 => base64 + "==",
            3 => base64 + "=",
            _ => throw new CryptographicException("The OAuth broker ticket encoding is invalid.")
        };
        try { return Convert.FromBase64String(base64); }
        catch (FormatException exception)
        {
            throw new CryptographicException("The OAuth broker ticket encoding is invalid.", exception);
        }
    }
}
