using System.Security.Cryptography;
using System.Text;

namespace TradeControl.Tax.UK.Adapters.Submission.Audit;

public enum SubmissionContentKind
{
    Payload,
    Response
}

public sealed record SubmissionContentStoreOptions(
    string RootPath,
    int MaximumPayloadBytes = 1_048_576,
    int MaximumResponseBytes = 262_144,
    TimeSpan? Retention = null);

public interface ISubmissionContentStore
{
    Task<string> StoreAsync(string tenantReference, string principalReference, SubmissionContentKind kind,
        ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);
    Task<byte[]?> ReadAsync(string tenantReference, string principalReference, string contentReference,
        CancellationToken cancellationToken = default);
}

public sealed class FileSubmissionContentStore : ISubmissionContentStore
{
    private readonly SubmissionContentStoreOptions _options;
    private readonly string _rootPrefix;
    private readonly TimeSpan _retention;

    public FileSubmissionContentStore(SubmissionContentStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.RootPath) || !Path.IsPathFullyQualified(options.RootPath))
            throw new ArgumentException("The protected content root must be absolute.", nameof(options));
        if (options.MaximumPayloadBytes < 1 || options.MaximumResponseBytes < 1)
            throw new ArgumentException("Protected content bounds must be positive.", nameof(options));
        _retention = options.Retention ?? TimeSpan.FromDays(30);
        if (_retention < TimeSpan.FromDays(1))
            throw new ArgumentException("Protected content retention must be at least one day.", nameof(options));
        _options = options with { RootPath = Path.GetFullPath(options.RootPath) };
        _rootPrefix = _options.RootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    }

    public async Task<string> StoreAsync(string tenantReference, string principalReference,
        SubmissionContentKind kind, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default)
    {
        ValidateReference(tenantReference, nameof(tenantReference));
        ValidateReference(principalReference, nameof(principalReference));
        var maximum = kind == SubmissionContentKind.Payload
            ? _options.MaximumPayloadBytes : _options.MaximumResponseBytes;
        if (content.Length == 0 || content.Length > maximum)
            throw new ArgumentException("Protected content is empty or exceeds its configured bound.", nameof(content));

        var kindName = kind == SubmissionContentKind.Payload ? "payload" : "response";
        var tenant = Fingerprint(tenantReference);
        var principal = Fingerprint(principalReference);
        var fileName = $"{Guid.NewGuid():N}.bin";
        var reference = $"{kindName}/{tenant}/{principal}/{fileName}";
        var path = ResolvePath(reference);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PruneExpiredContent();
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(true);
        return reference;
    }

    public async Task<byte[]?> ReadAsync(string tenantReference, string principalReference, string contentReference,
        CancellationToken cancellationToken = default)
    {
        ValidateReference(tenantReference, nameof(tenantReference));
        ValidateReference(principalReference, nameof(principalReference));
        ValidateReference(contentReference, nameof(contentReference));
        var segments = contentReference.Split('/');
        if (segments.Length != 4
            || segments[0] is not ("payload" or "response")
            || segments[1] != Fingerprint(tenantReference)
            || segments[2] != Fingerprint(principalReference)
            || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(segments[3]), "N", out _)
            || Path.GetExtension(segments[3]) != ".bin")
            return null;

        var path = ResolvePath(contentReference);
        if (!File.Exists(path)) return null;
        var info = new FileInfo(path);
        var maximum = segments[0] == "payload" ? _options.MaximumPayloadBytes : _options.MaximumResponseBytes;
        if (info.Length < 1 || info.Length > maximum)
            throw new InvalidDataException("Stored protected content violates its configured bound.");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private string ResolvePath(string reference)
    {
        if (reference.Contains("..", StringComparison.Ordinal) || reference.Contains('\\')
            || Path.IsPathFullyQualified(reference))
            throw new ArgumentException("The protected content reference is invalid.", nameof(reference));
        var path = Path.GetFullPath(Path.Combine(_options.RootPath,
            reference.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The protected content reference escaped its configured root.");
        return path;
    }

    private void PruneExpiredContent()
    {
        if (!Directory.Exists(_options.RootPath)) return;
        var cutoff = DateTime.UtcNow - _retention;
        foreach (var file in Directory.EnumerateFiles(_options.RootPath, "*.bin", SearchOption.AllDirectories))
        {
            var path = Path.GetFullPath(file);
            if (path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)
                && File.GetLastWriteTimeUtc(path) < cutoff)
                File.Delete(path);
        }
    }

    private static string Fingerprint(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 12));

    private static void ValidateReference(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256)
            throw new ArgumentException("A bounded content reference is required.", parameterName);
    }
}
