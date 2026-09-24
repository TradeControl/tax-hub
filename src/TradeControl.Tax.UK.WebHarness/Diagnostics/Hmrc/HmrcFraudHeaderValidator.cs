using System.Net.Http.Headers;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Hmrc;

public sealed record HmrcFraudHeaderValidationResponse(int StatusCode, string ContentType, byte[] Body)
{
    public override string ToString() =>
        $"HmrcFraudHeaderValidationResponse {{ StatusCode = {StatusCode}, Body = [REDACTED] }}";
}

public sealed class HmrcFraudHeaderValidator : IDisposable
{
    private const int MaximumResponseBytes = 128 * 1024;
    private readonly HttpClient _client;
    private readonly EnvironmentSelector _environment;
    private readonly bool _ownsClient;

    public HmrcFraudHeaderValidator(EnvironmentSelector environment)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true),
            environment, ownsClient: true) { }

    internal HmrcFraudHeaderValidator(HttpClient client, EnvironmentSelector environment, bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _ownsClient = ownsClient;
        if (_environment.Selected.Environment != AuthorityEnvironment.Sandbox)
            throw new InvalidOperationException("The fraud-header validator is sandbox-only.");
    }

    public async Task<HmrcFraudHeaderValidationResponse> ValidateAsync(FraudPreventionHeaders headers,
        ProtectedSecret accessToken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(accessToken);
        var tokenBuffer = new char[accessToken.Length];
        string token = string.Empty;
        try
        {
            accessToken.CopyTo(tokenBuffer);
            token = new string(tokenBuffer);
            using var request = new HttpRequestMessage(HttpMethod.Get,
                _environment.ResolveApiPath("/test/fraud-prevention-headers/validate"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.hmrc.1.0+json"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            foreach (var header in headers)
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
                    throw new InvalidOperationException("A formatted fraud-prevention header could not be added.");

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var body = await ReadBoundedAsync(response, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/json";
            return new((int)response.StatusCode, contentType, body);
        }
        finally
        {
            Array.Clear(tokenBuffer);
            token = string.Empty;
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The HMRC fraud-validator response exceeded its size limit.");
            output.Write(buffer, 0, read);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
