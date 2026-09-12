using System.Globalization;
using InactivePDF.Api;

namespace InactivePDF.UnitTests.Api;

public sealed class ApiKeyStoreTests
{
    [Fact]
    public void PersistsOnlyHashMetadataAndReloadsTheSecretForAuthentication()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "api-keys.json");
        try
        {
            var now = DateTimeOffset.Parse("2026-09-12T10:00:00Z", CultureInfo.InvariantCulture);
            var created = new ApiKeyStore(path).Create(new ApiKeyCreateRequest("worker"), now);
            var persisted = File.ReadAllText(path);

            Assert.DoesNotContain(created.Secret, persisted, StringComparison.Ordinal);
            Assert.Contains("Hash", persisted, StringComparison.Ordinal);
            Assert.Contains("Salt", persisted, StringComparison.Ordinal);
            Assert.DoesNotContain(".tmp", Directory.EnumerateFiles(root).Select(Path.GetFileName));

            var reloaded = new ApiKeyStore(path);
            Assert.True(reloaded.TryAuthenticate(created.Secret, now.AddSeconds(1), "127.0.0.1", out var metadata));
            Assert.Equal(created.Key.Id, metadata!.Id);
            Assert.Equal("127.0.0.1", metadata.LastSourceIp);
            Assert.Equal(now.AddSeconds(1), metadata.LastUsedAt);
            Assert.False(reloaded.TryAuthenticate("ipk_invalid", now, null, out _));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void ExpiryAndRevocationDisableAuthentication()
    {
        var root = CreateRoot();
        try
        {
            var store = new ApiKeyStore(Path.Combine(root, "api-keys.json"));
            var now = DateTimeOffset.UtcNow;
            var created = store.Create(new ApiKeyCreateRequest("short-lived", ExpiresAt: now.AddMinutes(1)), now);
            Assert.True(store.TryAuthenticate(created.Secret, now.AddSeconds(1), null, out _));
            Assert.False(store.TryAuthenticate(created.Secret, now.AddMinutes(2), null, out _));

            var revoked = store.Revoke(created.Key.Id, now.AddSeconds(2));
            Assert.NotNull(revoked);
            Assert.False(store.TryAuthenticate(created.Secret, now.AddSeconds(3), null, out _));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void RotationInvalidatesTheOldSecretAndReturnsTheReplacementOnlyOnce()
    {
        var root = CreateRoot();
        try
        {
            var store = new ApiKeyStore(Path.Combine(root, "api-keys.json"));
            var now = DateTimeOffset.UtcNow;
            var created = store.Create(new ApiKeyCreateRequest("rotating", [ApiKeyScopes.ConvertSubmit]), now);
            var rotated = store.Rotate(created.Key.Id, new ApiKeyRotateRequest([ApiKeyScopes.JobsRead]), now.AddMinutes(1));

            Assert.NotNull(rotated);
            Assert.NotEqual(created.Secret, rotated!.Secret);
            Assert.False(store.TryAuthenticate(created.Secret, now.AddMinutes(2), null, out _));
            Assert.True(store.TryAuthenticate(rotated.Secret, now.AddMinutes(2), null, out var metadata));
            Assert.Equal([ApiKeyScopes.JobsRead], metadata!.Scopes);
            Assert.DoesNotContain("Secret", store.Get(created.Key.Id)!.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void PersistsAndUpdatesPerKeyLimitsWithoutChangingTheSecret()
    {
        var root = CreateRoot();
        var path = Path.Combine(root, "api-keys.json");
        try
        {
            var store = new ApiKeyStore(path);
            var created = store.Create(new ApiKeyCreateRequest("limited", Limits: new ApiKeyLimits(RequestsPerMinute: 12, DailyInputBytes: 4096)), DateTimeOffset.UtcNow);
            var updated = store.UpdateLimits(created.Key.Id, created.Key.Limits with { ConcurrentConversions = 4 });

            Assert.Equal(4, updated!.Limits.ConcurrentConversions);
            Assert.Equal(12, updated.Limits.RequestsPerMinute);
            var reloaded = new ApiKeyStore(path);
            Assert.Equal(4, reloaded.Get(created.Key.Id)!.Limits.ConcurrentConversions);
            Assert.True(reloaded.TryAuthenticate(created.Secret, DateTimeOffset.UtcNow, null, out _));
        }
        finally { TryDelete(root); }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-api-key-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch (IOException) { }
    }
}
