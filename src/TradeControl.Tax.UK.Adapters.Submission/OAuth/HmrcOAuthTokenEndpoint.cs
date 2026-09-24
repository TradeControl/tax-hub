using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;

namespace TradeControl.Tax.UK.Adapters.Submission.OAuth;

public sealed class HmrcOAuthTokenEndpoint : IHmrcOAuthTokenEndpoint, IDisposable
{
    private const int MaximumResponseBytes = 64 * 1024;
    private readonly HttpClient _client;
    private readonly EnvironmentSelector _environment;
    private readonly bool _ownsClient;

    public HmrcOAuthTokenEndpoint(EnvironmentSelector environment)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true),
            environment, ownsClient: true) { }

    internal HmrcOAuthTokenEndpoint(HttpClient client, EnvironmentSelector environment, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _ownsClient = ownsClient;
    }

    public Task<OAuthTokenResponse> ExchangeCodeAsync(string clientId, string clientSecret, string code,
        string codeVerifier, Uri redirectUri, CancellationToken cancellationToken = default) => SendAsync(
        new Dictionary<string, string>
        {
            ["client_id"] = Required(clientId), ["client_secret"] = Required(clientSecret),
            ["grant_type"] = "authorization_code", ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code"] = Required(code), ["code_verifier"] = Required(codeVerifier)
        }, cancellationToken);

    public Task<OAuthTokenResponse> RefreshAsync(string clientId, string clientSecret, string refreshToken,
        CancellationToken cancellationToken = default) => SendAsync(new Dictionary<string, string>
        {
            ["client_id"] = Required(clientId), ["client_secret"] = Required(clientSecret),
            ["grant_type"] = "refresh_token", ["refresh_token"] = Required(refreshToken)
        }, cancellationToken);

    private async Task<OAuthTokenResponse> SendAsync(Dictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        var endpoint = _environment.ResolveApiPath("/oauth/token");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(bytes, cancellationToken);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new OAuthTokenEndpointException("TOKEN-RESPONSE-TOO-LARGE");
            buffer.Write(bytes, 0, read);
        }

        buffer.Position = 0;
        if (response.StatusCode != HttpStatusCode.OK)
        {
            var error = TryReadError(buffer) ?? $"HTTP-{(int)response.StatusCode}";
            throw new OAuthTokenEndpointException(error, error.Equals("invalid_grant", StringComparison.OrdinalIgnoreCase));
        }

        var payload = JsonSerializer.Deserialize<TokenPayload>(buffer.ToArray())
            ?? throw new OAuthTokenEndpointException("TOKEN-RESPONSE-INVALID");
        if (string.IsNullOrWhiteSpace(payload.AccessToken) || string.IsNullOrWhiteSpace(payload.RefreshToken)
            || payload.ExpiresIn <= 0 || !string.Equals(payload.TokenType, "bearer", StringComparison.OrdinalIgnoreCase))
            throw new OAuthTokenEndpointException("TOKEN-RESPONSE-INVALID");
        var scopes = (payload.Scope ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        return new(payload.AccessToken, payload.RefreshToken, TimeSpan.FromSeconds(payload.ExpiresIn), scopes);
    }

    private static string? TryReadError(MemoryStream stream)
    {
        try
        {
            stream.Position = 0;
            var error = JsonSerializer.Deserialize<ErrorPayload>(stream.ToArray())?.Error;
            return string.IsNullOrWhiteSpace(error) || error.Length > 64 ? null : error;
        }
        catch (JsonException) { return null; }
    }

    private static string Required(string value) => string.IsNullOrWhiteSpace(value)
        ? throw new ArgumentException("A required OAuth value is empty.") : value;

    private sealed record TokenPayload(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("scope")] string? Scope);
    private sealed record ErrorPayload([property: JsonPropertyName("error")] string? Error);

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
