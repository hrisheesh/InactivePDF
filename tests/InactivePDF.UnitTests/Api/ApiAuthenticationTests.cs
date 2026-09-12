using System.Net;
using System.Net.Http.Headers;
using InactivePDF.Api;
using Microsoft.AspNetCore.Http;

namespace InactivePDF.UnitTests.Api;

public sealed class ApiAuthenticationTests
{
    [Fact]
    public void ReadsOnlyBearerCredentials()
    {
        Assert.Equal("secret", ApiAuthentication.ReadBearerToken("Bearer secret"));
        Assert.Equal("secret", ApiAuthentication.ReadBearerToken("bearer secret"));
        Assert.Null(ApiAuthentication.ReadBearerToken("Basic secret"));
        Assert.Null(ApiAuthentication.ReadBearerToken("Bearer"));
        Assert.Null(ApiAuthentication.ReadBearerToken(null));
    }

    [Fact]
    public void SeparatesAdministratorAndIntegrationIdentities()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-auth-" + Guid.NewGuid().ToString("N"));
        var store = new ApiKeyStore(Path.Combine(root, "keys.json"));
        var created = store.Create(new ApiKeyCreateRequest("agent", [ApiKeyScopes.ProfilesRead]), DateTimeOffset.UtcNow);
        try
        {
            var adminContext = Context("Bearer admin-secret");
            var admin = ApiAuthentication.Authenticate(adminContext, store, "admin-secret", DateTimeOffset.UtcNow);
            Assert.Equal(ApiIdentityKind.Administrator, admin!.Kind);
            Assert.True(admin.IsAdministrator);

            var integrationContext = Context(new AuthenticationHeaderValue("Bearer", created.Secret).ToString());
            var integration = ApiAuthentication.Authenticate(integrationContext, store, "admin-secret", DateTimeOffset.UtcNow);
            Assert.Equal(ApiIdentityKind.Integration, integration!.Kind);
            Assert.Equal(created.Key.Id, integration.ApiKeyId);
            Assert.True(integration.HasScope(ApiKeyScopes.ProfilesRead));
            Assert.False(integration.HasScope(ApiKeyScopes.AdminRead));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MissingCredentialsUseLoopbackDevelopmentOnlyWhenAdminTokenIsNotConfigured()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-auth-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ApiKeyStore(Path.Combine(root, "keys.json"));
            var loopback = ApiAuthentication.Authenticate(Context(null, IPAddress.Loopback), store, null, DateTimeOffset.UtcNow);
            Assert.Equal(ApiIdentityKind.LoopbackDevelopment, loopback!.Kind);
            Assert.Null(ApiAuthentication.Authenticate(Context(null, IPAddress.Loopback), store, "configured", DateTimeOffset.UtcNow));
            Assert.Null(ApiAuthentication.Authenticate(Context(null, IPAddress.Parse("192.0.2.1")), store, null, DateTimeOffset.UtcNow));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    private static DefaultHttpContext Context(string? authorization, IPAddress? address = null)
    {
        var context = new DefaultHttpContext();
        if (authorization is not null) context.Request.Headers.Authorization = authorization;
        context.Connection.RemoteIpAddress = address ?? IPAddress.Loopback;
        return context;
    }

    [Fact]
    public void ExpiredAndRevokedKeysCannotAuthenticate()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-auth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new ApiKeyStore(Path.Combine(root, "keys.json"));
            var now = DateTimeOffset.UtcNow;
            var expiring = store.Create(new ApiKeyCreateRequest("expiring", ExpiresAt: now.AddMinutes(1)), now);
            Assert.NotNull(ApiAuthentication.Authenticate(Context("Bearer " + expiring.Secret), store, "admin", now));
            Assert.Null(ApiAuthentication.Authenticate(Context("Bearer " + expiring.Secret), store, "admin", now.AddMinutes(1)));

            var revocable = store.Create(new ApiKeyCreateRequest("revocable"), now);
            Assert.NotNull(store.Revoke(revocable.Key.Id, now.AddSeconds(1)));
            Assert.Null(ApiAuthentication.Authenticate(Context("Bearer " + revocable.Secret), store, "admin", now.AddSeconds(2)));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
