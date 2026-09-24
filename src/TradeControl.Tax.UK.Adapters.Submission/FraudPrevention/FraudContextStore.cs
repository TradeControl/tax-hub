using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeControl.Tax.UK.Adapters.Submission.FraudPrevention;

internal interface IFraudContextStore : IDisposable
{
    Task<SealedFraudContextReference> StoreAsync(FraudContextSnapshot snapshot,
        CancellationToken cancellationToken = default);
    Task<FraudContextSnapshot?> ReadAsync(SealedFraudContextReference reference, FraudActorIdentity identity,
        CancellationToken cancellationToken = default);
}

internal sealed class EncryptedFileFraudContextStore : IFraudContextStore
{
    private const int MaximumPlaintextBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _clock;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private byte[]? _key;

    public EncryptedFileFraudContextStore(string root, ReadOnlySpan<byte> key, TimeSpan retention,
        TimeProvider clock)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            throw new ArgumentException("An absolute protected fraud-context root is required.", nameof(root));
        if (key.Length != 32) throw new ArgumentException("A 256-bit fraud-context key is required.", nameof(key));
        _root = Path.GetFullPath(root);
        _key = key.ToArray();
        _retention = retention;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<SealedFraudContextReference> StoreAsync(FraudContextSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var reference = new SealedFraudContextReference(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        var identity = new FraudActorIdentity(snapshot.TenantReference, snapshot.PrincipalReference, snapshot.ActorReference).Validate();
        var path = ResolvePath(reference, identity);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(snapshot, _json);
        if (plaintext.Length > MaximumPlaintextBytes)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new FraudContextRejectedException("The sealed fraud context exceeds its storage bound.");
        }
        byte[] encrypted;
        try { encrypted = Encrypt(plaintext, AssociatedData(reference, identity)); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        await using var lease = await FileLease.AcquireAsync($"{path}.lock", cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PruneExpired(Path.GetDirectoryName(path)!);
        var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            CryptographicOperations.ZeroMemory(encrypted);
        }
        return reference;
    }

    public async Task<FraudContextSnapshot?> ReadAsync(SealedFraudContextReference reference,
        FraudActorIdentity identity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        identity = (identity ?? throw new ArgumentNullException(nameof(identity))).Validate();
        var path = ResolvePath(reference, identity);
        await using var lease = await FileLease.AcquireAsync($"{path}.lock", cancellationToken);
        if (!File.Exists(path)) return null;
        if (File.GetLastWriteTimeUtc(path) < _clock.GetUtcNow().UtcDateTime - _retention)
        {
            File.Delete(path);
            return null;
        }
        var envelope = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[] plaintext;
        try { plaintext = Decrypt(envelope, AssociatedData(reference, identity)); }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException)
        {
            throw new FraudContextRejectedException("The sealed fraud context failed integrity validation.");
        }
        finally { CryptographicOperations.ZeroMemory(envelope); }
        try
        {
            var snapshot = JsonSerializer.Deserialize<FraudContextSnapshot>(plaintext, _json)
                ?? throw new FraudContextRejectedException("The sealed fraud context is invalid.");
            if (snapshot.TenantReference != identity.TenantReference
                || snapshot.PrincipalReference != identity.PrincipalReference
                || snapshot.ActorReference != identity.ActorReference)
                throw new FraudContextRejectedException("The sealed fraud context ownership is invalid.");
            return snapshot;
        }
        catch (JsonException) { throw new FraudContextRejectedException("The sealed fraud context is invalid."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private string ResolvePath(SealedFraudContextReference reference, FraudActorIdentity identity)
    {
        var owner = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity.TenantReference}\n{identity.PrincipalReference}")))[..16];
        return Path.Combine(_root, owner, $"{reference.Value}.fraud.enc");
    }

    private void PruneExpired(string ownerDirectory)
    {
        var cutoff = _clock.GetUtcNow().UtcDateTime - _retention;
        foreach (var path in Directory.GetFiles(ownerDirectory, "*.fraud.enc", SearchOption.TopDirectoryOnly))
            if (File.GetLastWriteTimeUtc(path) < cutoff) File.Delete(path);
    }

    private byte[] AssociatedData(SealedFraudContextReference reference, FraudActorIdentity identity) =>
        Encoding.UTF8.GetBytes($"tax-hub-fraud-v1\n{identity.TenantReference}\n{identity.PrincipalReference}\n{identity.ActorReference}\n{reference.Value}");

    private byte[] Encrypt(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(EncryptedFileFraudContextStore));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(
            Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)));
    }

    private byte[] Decrypt(ReadOnlySpan<byte> envelopeBytes, ReadOnlySpan<byte> associatedData)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(EncryptedFileFraudContextStore));
        var envelope = JsonSerializer.Deserialize<Envelope>(envelopeBytes)
            ?? throw new CryptographicException("Invalid envelope.");
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        return plaintext;
    }

    public void Dispose()
    {
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    private sealed record Envelope(string Nonce, string Ciphertext, string Tag);

    private sealed class FileLease : IAsyncDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private readonly FileStream _stream;
        private FileLease(SemaphoreSlim semaphore, FileStream stream) { _semaphore = semaphore; _stream = stream; }
        public static async Task<FileLease> AcquireAsync(string path, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var semaphore = ProcessLocks.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                while (true)
                {
                    try { return new FileLease(semaphore, new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)); }
                    catch (IOException) { await Task.Delay(25, cancellationToken); }
                }
            }
            catch { semaphore.Release(); throw; }
        }
        public ValueTask DisposeAsync() { _stream.Dispose(); _semaphore.Release(); return ValueTask.CompletedTask; }
    }
}
