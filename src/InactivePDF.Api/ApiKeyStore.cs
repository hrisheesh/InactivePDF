using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

/// <summary>Durable integration-key metadata and salted secret hashes.</summary>
public sealed class ApiKeyStore
{
    private const int SaltBytes = 16;
    private const int HashBytes = 64;
    private const int Pbkdf2Iterations = 600_000;
    private const string SecretPrefix = "ipk_";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object _gate = new();
    private readonly string _path;
    private Dictionary<Guid, PersistedApiKey> _keys;

    public ApiKeyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(_path) ?? throw new ArgumentException("The API-key path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        EnsureSafeStoragePath();
        _keys = Load();
    }

    public IReadOnlyList<ApiKeyMetadata> List()
    {
        lock (_gate) return _keys.Values.Select(ToMetadata).OrderBy(key => key.CreatedAt).ToArray();
    }

    public ApiKeyMetadata? Get(Guid id)
    {
        lock (_gate) return _keys.TryGetValue(id, out var key) ? ToMetadata(key) : null;
    }

    public ApiKeySecretResponse Create(ApiKeyCreateRequest request, DateTimeOffset now)
    {
        var name = NormalizeName(request.Name);
        var scopes = ApiKeyScopes.Normalize(request.Scopes);
        var limits = request.Limits ?? ApiKeyLimits.Default;
        limits.Validate();
        ValidateExpiry(request.ExpiresAt, now);
        lock (_gate)
        {
            if (_keys.Values.Any(key => string.Equals(key.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw new ApiKeyNameConflictException(name);

            var secret = CreateSecret();
            var record = CreateRecord(name, scopes, request.ExpiresAt, now, secret, limits);
            _keys.Add(record.Id, record);
            Persist();
            return new ApiKeySecretResponse(ToMetadata(record), secret);
        }
    }

    public ApiKeySecretResponse? Rotate(Guid id, ApiKeyRotateRequest? request, DateTimeOffset now)
    {
        request ??= new ApiKeyRotateRequest();
        lock (_gate)
        {
            if (!_keys.TryGetValue(id, out var existing)) return null;
            var scopes = ApiKeyScopes.Normalize(request.Scopes ?? existing.Scopes);
            var limits = request.Limits ?? existing.Limits ?? ApiKeyLimits.Default;
            limits.Validate();
            var expiresAt = request.ExpiresAt ?? existing.ExpiresAt;
            ValidateExpiry(expiresAt, now);
            var secret = CreateSecret();
            var replacement = CreateRecord(existing.Name, scopes, expiresAt, existing.CreatedAt, secret, limits) with { Id = id };
            _keys[id] = replacement;
            Persist();
            return new ApiKeySecretResponse(ToMetadata(replacement), secret);
        }
    }

    public ApiKeyMetadata? Revoke(Guid id, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_keys.TryGetValue(id, out var existing)) return null;
            if (existing.RevokedAt is null)
            {
                existing = existing with { RevokedAt = now };
                _keys[id] = existing;
                Persist();
            }
            return ToMetadata(existing);
        }
    }

    public ApiKeyMetadata? UpdateLimits(Guid id, ApiKeyLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        lock (_gate)
        {
            if (!_keys.TryGetValue(id, out var existing)) return null;
            var updated = existing with { Limits = limits };
            _keys[id] = updated;
            Persist();
            return ToMetadata(updated);
        }
    }

    public bool Delete(Guid id)
    {
        lock (_gate)
        {
            if (!_keys.Remove(id)) return false;
            Persist();
            return true;
        }
    }

    public bool TryAuthenticate(string secret, DateTimeOffset now, string? sourceIp, out ApiKeyMetadata? metadata)
    {
        metadata = null;
        if (!TryGetPrefix(secret, out var prefix)) return false;
        lock (_gate)
        {
            var candidate = _keys.Values.FirstOrDefault(key => string.Equals(key.Prefix, prefix, StringComparison.Ordinal));
            if (candidate is null || candidate.RevokedAt is not null || candidate.ExpiresAt is { } expiry && expiry <= now || !Verify(secret, candidate)) return false;

            var used = candidate with { LastUsedAt = now, LastSourceIp = NormalizeSourceIp(sourceIp) };
            _keys[candidate.Id] = used;
            Persist();
            metadata = ToMetadata(used);
            return true;
        }
    }

    private Dictionary<Guid, PersistedApiKey> Load()
    {
        if (!File.Exists(_path)) return new();
        EnsureSafeStoragePath();
        try
        {
            var entries = JsonSerializer.Deserialize<List<PersistedApiKey>>(File.ReadAllText(_path), JsonOptions) ?? [];
            return entries.ToDictionary(entry => entry.Id);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The API-key store '{_path}' is not valid JSON.", exception);
        }
    }

    private void Persist()
    {
        EnsureSafeStoragePath();
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, _keys.Values.OrderBy(key => key.CreatedAt).ToArray(), JsonOptions);
                stream.Flush(true);
            }
            EnsureSafeStoragePath();
            if (File.Exists(_path))
            {
                try { File.Replace(temporary, _path, destinationBackupFileName: null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temporary, _path, overwrite: true); }
            }
            else File.Move(temporary, _path);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }

    private void EnsureSafeStoragePath()
    {
        var directory = Path.GetDirectoryName(_path)!;
        WorkspacePathSecurity.EnsureSafeChain(directory, directory);
        if (File.Exists(_path) && File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("The API-key store cannot be a symbolic link or reparse point.");
    }

    private static PersistedApiKey CreateRecord(string name, string[] scopes, DateTimeOffset? expiresAt, DateTimeOffset createdAt, string secret, ApiKeyLimits limits)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Hash(secret, salt);
        return new PersistedApiKey(Guid.NewGuid(), name, CreatePrefix(secret), Convert.ToBase64String(salt), Convert.ToBase64String(hash), scopes, createdAt, expiresAt, null, null, null, limits);
    }

    private static string CreateSecret() => SecretPrefix + Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string CreatePrefix(string secret) => secret.Length <= 14 ? secret : secret[..14];

    private static bool TryGetPrefix(string secret, out string prefix)
    {
        prefix = string.Empty;
        if (string.IsNullOrWhiteSpace(secret) || !secret.StartsWith(SecretPrefix, StringComparison.Ordinal) || secret.Length < 15) return false;
        prefix = CreatePrefix(secret);
        return true;
    }

    private static bool Verify(string secret, PersistedApiKey record)
    {
        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(record.Salt);
            expected = Convert.FromBase64String(record.Hash);
        }
        catch (FormatException) { return false; }
        var actual = Hash(secret, salt);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static byte[] Hash(string secret, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, HashBytes);

    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string NormalizeName(string? name)
    {
        var normalized = name?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64 || normalized.Any(char.IsControl))
            throw new ArgumentException("API-key names must contain 1–64 non-control characters.", nameof(name));
        return normalized;
    }

    private static void ValidateExpiry(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        if (expiresAt is { } expiry && expiry <= now)
            throw new ArgumentException("API-key expiry must be in the future.", nameof(expiresAt));
    }

    private static string? NormalizeSourceIp(string? sourceIp) => sourceIp is { Length: > 128 } ? sourceIp[..128] : sourceIp;

    private static ApiKeyMetadata ToMetadata(PersistedApiKey key) => new(key.Id, key.Name, key.Prefix, key.Scopes, key.CreatedAt, key.ExpiresAt, key.RevokedAt, key.LastUsedAt, key.LastSourceIp, key.Limits ?? ApiKeyLimits.Default);

    private sealed record PersistedApiKey(
        Guid Id,
        string Name,
        string Prefix,
        string Salt,
        string Hash,
        string[] Scopes,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ExpiresAt,
        DateTimeOffset? RevokedAt,
        DateTimeOffset? LastUsedAt,
        string? LastSourceIp,
        ApiKeyLimits? Limits = null);
}
