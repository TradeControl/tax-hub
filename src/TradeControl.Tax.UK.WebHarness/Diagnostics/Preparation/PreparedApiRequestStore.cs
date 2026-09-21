using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;

public sealed record PreparedApiRequestStoreOptions(int Capacity, TimeSpan Retention, string? OutputDirectory = null)
{
    public static PreparedApiRequestStoreOptions Development(string contentRoot, IConfiguration configuration)
    {
        var capacity = configuration.GetValue("TaxHub:PreparationStore:Capacity", 100);
        var retentionMinutes = configuration.GetValue("TaxHub:PreparationStore:RetentionMinutes", 30);
        var output = configuration["TaxHub:HarnessOutputDirectory"]
            ?? Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "..", "..",
                ".local", "sandbox", "tax-hub", "prepared"));
        return new(capacity, TimeSpan.FromMinutes(retentionMinutes), output);
    }
}

public sealed class PreparedApiRequestStore
{
    private sealed record Entry(PreparedApiRequest Request, DateTimeOffset ExpiresAt, long Sequence);
    private readonly Dictionary<string, Entry> _requests = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly PreparedApiRequestStoreOptions _options;
    private readonly TimeProvider _time;
    private long _sequence;

    public PreparedApiRequestStore(PreparedApiRequestStoreOptions options, TimeProvider? timeProvider = null)
    {
        if (options.Capacity < 1) throw new ArgumentOutOfRangeException(nameof(options), "Capacity must be positive.");
        if (options.Retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Retention must be positive.");
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<string> SaveAsync(PreparedApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = _time.GetUtcNow();
        var id = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            RemoveExpired(now);
            while (_requests.Count >= _options.Capacity)
            {
                var oldest = _requests.MinBy(item => item.Value.Sequence).Key;
                _requests.Remove(oldest);
            }
            _requests.Add(id, new(request, now + _options.Retention, ++_sequence));
        }

        if (!string.IsNullOrWhiteSpace(_options.OutputDirectory))
            await PersistAsync(id, request, _options.OutputDirectory, cancellationToken);
        return id;
    }

    public bool TryGet(string id, out PreparedApiRequest request)
    {
        request = null!;
        if (!IsOpaqueId(id)) return false;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            RemoveExpired(now);
            if (!_requests.TryGetValue(id, out var entry)) return false;
            request = entry.Request;
            return true;
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var id in _requests.Where(item => item.Value.ExpiresAt <= now).Select(item => item.Key).ToArray())
            _requests.Remove(id);
    }

    private static bool IsOpaqueId(string? id) => id is { Length: 32 }
        && id.All(character => char.IsAsciiHexDigit(character));

    private static async Task PersistAsync(string id, PreparedApiRequest request, string outputDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        var metadata = PreparedApiRequestInspection.From(id, request);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, $"{id}.json"),
            JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        if (request.BodyBytes is { } bytes)
            await File.WriteAllBytesAsync(Path.Combine(outputDirectory, $"{id}.body"), bytes.ToArray(), cancellationToken);
    }
}

public sealed record PreparedApiRequestInspection(
    string PreparationId,
    string OperationId,
    string ContractFamily,
    string ContractVersion,
    bool HarnessPreview,
    bool AuthorityPreviewContract,
    string Method,
    string RelativePath,
    IReadOnlyList<PreparedNameValue> Query,
    IReadOnlyList<PreparedNameValue> Headers,
    string? ContentType,
    int? BodyLength,
    string? BodySha256,
    IReadOnlyList<PreparedSourceEvidence> SourceEvidence,
    IReadOnlyList<PreparedArtifactFinding> Findings)
{
    public static PreparedApiRequestInspection From(string id, PreparedApiRequest request) => new(
        id, request.OperationId, request.ContractFamily, request.ContractVersion, true, request.IsPreview,
        request.Method, request.RelativePath, request.Query, request.Headers, request.ContentType,
        request.BodyBytes?.Length, request.BodySha256, request.SourceEvidence, request.Findings);
}

public static class PreparedApiRequestHttp
{
    public static async Task WriteBodyAsync(HttpResponse response, PreparedApiRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.BodyBytes is not { } body)
            throw new InvalidOperationException("The prepared request has no sendable body.");
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = request.ContentType ?? "application/octet-stream";
        response.ContentLength = body.Length;
        response.Headers["X-TaxHub-Preview"] = "true";
        await response.Body.WriteAsync(body.ToArray(), cancellationToken);
    }
}

public static class PreparedRequestProblem
{
    public static ProblemDetails FromException(Exception exception, string correlationId)
    {
        var (status, title) = exception switch
        {
            ArgumentException => (StatusCodes.Status400BadRequest, "The request is invalid."),
            InvalidOperationException => (StatusCodes.Status422UnprocessableEntity, "The request could not be prepared."),
            _ => (StatusCodes.Status500InternalServerError, "The request failed unexpectedly.")
        };
        return new()
        {
            Status = status,
            Title = title,
            Detail = "No prepared request was created.",
            Extensions = { ["correlationId"] = correlationId }
        };
    }
}
