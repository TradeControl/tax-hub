using System.Net.Http.Headers;
using TradeControl.Tax.UK.Adapters.Submission.Configuration;
using TradeControl.Tax.UK.Adapters.Submission.OAuth;

namespace TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;

public sealed record HmrcFraudPreventionValidationResult(int StatusCode, string ContentType, byte[] Body)
{
    public override string ToString() =>
        $"HmrcFraudPreventionValidationResult {{ StatusCode = {StatusCode}, Body = [REDACTED] }}";
}

/// <summary>
/// Calls HMRC's sandbox-only fraud-prevention-header validator with headers produced by the
/// same product composition used for ordinary authority requests.
/// </summary>
public sealed class HmrcFraudPreventionValidator : IDisposable
{
    private const int MaximumResponseBytes = 128 * 1024;
    private readonly HttpClient _client;
    private readonly EnvironmentSelector _environment;
    private readonly bool _ownsClient;

    public HmrcFraudPreventionValidator(EnvironmentSelector environment)
        : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }, disposeHandler: true),
            environment, ownsClient: true) { }

    internal HmrcFraudPreventionValidator(HttpClient client, EnvironmentSelector environment,
        bool ownsClient = false)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _ownsClient = ownsClient;
        if (_environment.Selected.Environment != AuthorityEnvironment.Sandbox)
            throw new InvalidOperationException("The fraud-header validator is sandbox-only.");
    }

    public async Task<HmrcFraudPreventionValidationResult> ValidateAsync(
        FraudPreventionHeaders headers, ProtectedSecret accessToken,
        CancellationToken cancellationToken = default)
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
            return new((int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "application/json", body);
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
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("The HMRC fraud-validator response exceeded its size limit.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("The HMRC fraud-validator response exceeded its size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _client.Dispose();
    }
}
