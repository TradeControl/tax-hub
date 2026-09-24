using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeControl.Tax.UK.Adapters.Submission.OAuth;

internal sealed record PendingOAuthAuthorisation(
    string State, string CodeVerifier, string TenantReference, string PrincipalReference,
    string ActorReference, string RequiredScope, string RedirectUri, DateTimeOffset ExpiresAtUtc);

internal sealed record StoredOAuthGrant(
    string TenantReference, string PrincipalReference, string RequiredScope,
    string AccessToken, string RefreshToken, HashSet<string> Scopes,
    DateTimeOffset AccessTokenExpiresAtUtc, DateTimeOffset GrantExpiresAtUtc, bool Revoked = false);

internal interface IOAuthGrantStore
{
    Task SavePendingAsync(PendingOAuthAuthorisation pending, CancellationToken cancellationToken = default);
    Task<PendingOAuthAuthorisation?> ConsumePendingAsync(string state, string tenantReference,
        string principalReference, string actorReference, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default);
    Task<StoredOAuthGrant?> GetGrantAsync(string tenantReference, string principalReference,
        string requiredScope, CancellationToken cancellationToken = default);
    Task SaveGrantAsync(StoredOAuthGrant grant, CancellationToken cancellationToken = default);
    Task RevokeAsync(string tenantReference, string principalReference, string requiredScope,
        CancellationToken cancellationToken = default);
    Task<IAsyncDisposable> AcquireRefreshLeaseAsync(string tenantReference, string principalReference,
        string requiredScope, CancellationToken cancellationToken = default);
}

internal sealed class AesGcmOAuthStoreCipher : IDisposable
{
    private byte[]? _key;

    public AesGcmOAuthStoreCipher(ReadOnlySpan<byte> key)
    {
        if (key.Length != 32) throw new ArgumentException("A 256-bit OAuth store key is required.", nameof(key));
        _key = key.ToArray();
    }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(AesGcmOAuthStoreCipher));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(key, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        return JsonSerializer.SerializeToUtf8Bytes(new Envelope(
            Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag)));
    }

    public byte[] Decrypt(ReadOnlySpan<byte> envelopeBytes)
    {
        var key = _key ?? throw new ObjectDisposedException(nameof(AesGcmOAuthStoreCipher));
        var envelope = JsonSerializer.Deserialize<Envelope>(envelopeBytes)
            ?? throw new CryptographicException("The OAuth store envelope is invalid.");
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(key, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }

    public void Dispose()
    {
        if (_key is null) return;
        CryptographicOperations.ZeroMemory(_key);
        _key = null;
    }

    private sealed record Envelope(string Nonce, string Ciphertext, string Tag);
}

internal sealed class FileOAuthGrantStore : IOAuthGrantStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> ProcessLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _path;
    private readonly AesGcmOAuthStoreCipher _cipher;
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    public FileOAuthGrantStore(string path, AesGcmOAuthStoreCipher cipher)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("An absolute OAuth grant-store path is required.", nameof(path));
        _path = Path.GetFullPath(path);
        _cipher = cipher ?? throw new ArgumentNullException(nameof(cipher));
    }

    public Task SavePendingAsync(PendingOAuthAuthorisation pending, CancellationToken cancellationToken = default) =>
        MutateAsync(document =>
        {
            if (document.Pending.Count >= 1_000)
                throw new InvalidOperationException("The pending OAuth authorization store is full.");
            document.Pending.Add(pending);
            return 0;
        }, cancellationToken);

    public Task<PendingOAuthAuthorisation?> ConsumePendingAsync(string state, string tenantReference,
        string principalReference, string actorReference, DateTimeOffset nowUtc,
        CancellationToken cancellationToken = default) => MutateAsync(document =>
        {
            document.Pending.RemoveAll(item => item.ExpiresAtUtc <= nowUtc);
            var index = document.Pending.FindIndex(item => FixedEquals(item.State, state)
                && item.TenantReference == tenantReference && item.PrincipalReference == principalReference
                && item.ActorReference == actorReference);
            if (index < 0) return null;
            var pending = document.Pending[index];
            document.Pending.RemoveAt(index);
            return pending;
        }, cancellationToken);

    public Task<StoredOAuthGrant?> GetGrantAsync(string tenantReference, string principalReference,
        string requiredScope, CancellationToken cancellationToken = default) => MutateAsync(document =>
        document.Grants.SingleOrDefault(item => item.TenantReference == tenantReference
            && item.PrincipalReference == principalReference && item.RequiredScope == requiredScope), cancellationToken);

    public Task SaveGrantAsync(StoredOAuthGrant grant, CancellationToken cancellationToken = default) =>
        MutateAsync(document =>
        {
            document.Grants.RemoveAll(item => item.TenantReference == grant.TenantReference
                && item.PrincipalReference == grant.PrincipalReference && item.RequiredScope == grant.RequiredScope);
            document.Grants.Add(grant);
            return 0;
        }, cancellationToken);

    public Task RevokeAsync(string tenantReference, string principalReference, string requiredScope,
        CancellationToken cancellationToken = default) => MutateAsync(document =>
        {
            var index = document.Grants.FindIndex(item => item.TenantReference == tenantReference
                && item.PrincipalReference == principalReference && item.RequiredScope == requiredScope);
            if (index >= 0) document.Grants[index] = document.Grants[index] with { Revoked = true, AccessToken = "-", RefreshToken = "-" };
            return 0;
        }, cancellationToken);

    public async Task<IAsyncDisposable> AcquireRefreshLeaseAsync(string tenantReference, string principalReference,
        string requiredScope, CancellationToken cancellationToken = default)
    {
        var leaseId = Fingerprint($"{tenantReference}\n{principalReference}\n{requiredScope}");
        var leasePath = $"{_path}.{leaseId}.refresh.lock";
        return await FileLease.AcquireAsync(leasePath, cancellationToken);
    }

    private async Task<T> MutateAsync<T>(Func<OAuthDocument, T> action, CancellationToken cancellationToken)
    {
        await using var lease = await FileLease.AcquireAsync($"{_path}.lock", cancellationToken);
        var document = await ReadAsync(cancellationToken);
        var result = action(document);
        await WriteAsync(document, cancellationToken);
        return result;
    }

    private async Task<OAuthDocument> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return new();
        var encrypted = await File.ReadAllBytesAsync(_path, cancellationToken);
        var plaintext = _cipher.Decrypt(encrypted);
        try { return JsonSerializer.Deserialize<OAuthDocument>(plaintext, _json) ?? new(); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private async Task WriteAsync(OAuthDocument document, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)!;
        Directory.CreateDirectory(directory);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, _json);
        byte[] encrypted;
        try { encrypted = _cipher.Encrypt(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(encrypted, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var b = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];

    private sealed class OAuthDocument
    {
        public List<PendingOAuthAuthorisation> Pending { get; set; } = [];
        public List<StoredOAuthGrant> Grants { get; set; } = [];
    }

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

        public ValueTask DisposeAsync()
        {
            _stream.Dispose();
            _semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}
