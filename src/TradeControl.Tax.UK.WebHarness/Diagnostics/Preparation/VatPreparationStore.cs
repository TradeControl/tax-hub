using System.Collections.Concurrent;
using System.Text.Json;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;

public sealed class VatPreparationStore
{
    private readonly ConcurrentDictionary<string, PreparedApiRequest> _requests = new();
    private readonly string _outputDirectory;

    public VatPreparationStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _outputDirectory = configuration["TaxHub:HarnessOutputDirectory"]
            ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "..", "..",
                ".local", "sandbox", "tax-hub", "vat"));
    }

    public async Task<string> SaveAsync(PreparedApiRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        _requests[id] = request;
        Directory.CreateDirectory(_outputDirectory);
        var metadata = PreparedApiRequestInspection.From(id, request);
        await File.WriteAllBytesAsync(Path.Combine(_outputDirectory, $"{id}.json"),
            JsonSerializer.SerializeToUtf8Bytes(metadata, new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
        if (request.BodyBytes is { } bytes)
            await File.WriteAllBytesAsync(Path.Combine(_outputDirectory, $"{id}.body.json"), bytes.ToArray(), cancellationToken);
        return id;
    }

    public bool TryGet(string id, out PreparedApiRequest request) => _requests.TryGetValue(id, out request!);
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
