using System.Collections.Concurrent;
using System.Text.Json;
using TradeControl.Tax.UK.Application.Preparation;

namespace TradeControl.Tax.UK.WebHarness.Diagnostics.Preparation;

public sealed class CumulativePreparationStore
{
    private readonly ConcurrentDictionary<string, PreparedApiRequest> _requests = new();
    private readonly string _outputDirectory;

    public CumulativePreparationStore(IWebHostEnvironment environment, IConfiguration configuration)
    {
        _outputDirectory = configuration["TaxHub:CumulativeHarnessOutputDirectory"]
            ?? Path.GetFullPath(Path.Combine(environment.ContentRootPath, "..", "..", "..", "..",
                ".local", "sandbox", "tax-hub", "mtd-income-tax"));
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
            await File.WriteAllBytesAsync(Path.Combine(_outputDirectory, $"{id}.body.json"),
                bytes.ToArray(), cancellationToken);
        return id;
    }

    public bool TryGet(string id, out PreparedApiRequest request) => _requests.TryGetValue(id, out request!);
}
