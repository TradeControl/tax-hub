using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace TradeControl.Tax.UK.Adapters.Submission.TransactionEngine;

internal sealed record TransactionEngineHttpGatewayOptions(
    int MaximumResponseBytes = 8 * 1024 * 1024,
    int TimeoutSeconds = 180);

internal sealed record TransactionEngineHttpExchange(
    HttpStatusCode StatusCode,
    ImmutableArray<byte> Content,
    string ResponseSha256);

/// <summary>
/// One-attempt HTTP exchange boundary. Conversation state, recovery, polling and
/// retry decisions remain with the durable Transaction Engine coordinator.
/// </summary>
internal sealed class TransactionEngineHttpGateway(
    HttpClient client,
    TransactionEngineProtocolOptions protocol,
    TransactionEngineHttpGatewayOptions options)
{
    internal static HttpClient CreateExternalTestClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    });

    internal async Task<TransactionEngineHttpExchange> ExchangeOnceAsync(
        TransactionEngineOutboundMessage outbound,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        if (options.MaximumResponseBytes is < 1024 or > 32 * 1024 * 1024
            || options.TimeoutSeconds is < 1 or > 600)
            throw new ArgumentException("The Transaction Engine HTTP bounds are invalid.", nameof(options));
        if (outbound.Content.IsDefaultOrEmpty
            || outbound.Content.Length > protocol.MaximumXmlBytes
            || Convert.ToHexString(SHA256.HashData(outbound.Content.AsSpan())) != outbound.Sha256)
            throw new InvalidDataException("The outbound Transaction Engine evidence failed digest verification.");
        var endpoint = TransactionEngineWire.ValidateResponseEndpoint(
            outbound.Endpoint.AbsoluteUri, protocol);
        if (outbound.Kind is TransactionEngineOutboundKind.Submit or TransactionEngineOutboundKind.DataRequest
            && endpoint != protocol.SubmissionEndpoint)
            throw new InvalidDataException("A submission or DATA_REQUEST must use the pinned Transaction Engine endpoint.");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(outbound.Content.ToArray())
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml")
        {
            CharSet = "utf-8"
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The one-attempt Transaction Engine exchange timed out; its outcome is uncertain.", exception);
        }
        using (response)
        {
            if ((int)response.StatusCode is >= 300 and < 400)
                throw new InvalidDataException("The Transaction Engine returned a redirect; redirects are not followed.");
            if (response.Content.Headers.ContentLength > options.MaximumResponseBytes)
                throw new InvalidDataException("The Transaction Engine response exceeds its configured bound.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
                var count = await stream.ReadAsync(chunk, timeout.Token);
                if (count == 0) break;
                if (buffer.Length + count > options.MaximumResponseBytes)
                    throw new InvalidDataException("The Transaction Engine response exceeds its configured bound.");
                buffer.Write(chunk, 0, count);
            }
            var content = buffer.ToArray();
            return new(response.StatusCode, ImmutableArray.Create(content),
                Convert.ToHexString(SHA256.HashData(content)));
        }
    }
}
