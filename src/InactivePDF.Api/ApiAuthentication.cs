using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace InactivePDF.Api;

public enum ApiIdentityKind
{
    Administrator,
    Integration,
    LoopbackDevelopment
}

public sealed record ApiIdentity(ApiIdentityKind Kind, Guid? ApiKeyId, string[] Scopes)
{
    public bool IsAdministrator => Kind is ApiIdentityKind.Administrator or ApiIdentityKind.LoopbackDevelopment;

    public bool HasScope(string scope) => IsAdministrator || Scopes.Contains(scope, StringComparer.Ordinal);
}

public static class ApiAuthentication
{
    internal const string IdentityItem = "InactivePDF.Api.Identity";

    internal static ApiIdentity? GetIdentity(HttpContext context) => context.Items[IdentityItem] as ApiIdentity;

    public static ApiIdentity? Authenticate(HttpContext context, ApiKeyStore keyStore, string? administratorToken, DateTimeOffset now)
    {
        var supplied = ReadBearerToken(context.Request.Headers.Authorization.ToString());
        if (supplied is null)
        {
            return string.IsNullOrWhiteSpace(administratorToken) && IsLoopback(context)
                ? new ApiIdentity(ApiIdentityKind.LoopbackDevelopment, null, [])
                : null;
        }

        if (!string.IsNullOrWhiteSpace(administratorToken) && ConstantTimeEquals(supplied, administratorToken))
            return new ApiIdentity(ApiIdentityKind.Administrator, null, []);

        return keyStore.TryAuthenticate(supplied, now, context.Connection.RemoteIpAddress?.ToString(), out var metadata) && metadata is not null
            ? new ApiIdentity(ApiIdentityKind.Integration, metadata.Id, metadata.Scopes.ToArray())
            : null;
    }

    public static string? ReadBearerToken(string? authorization)
    {
        if (string.IsNullOrWhiteSpace(authorization) || !AuthenticationHeaderValue.TryParse(authorization, out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(header.Parameter))
            return null;
        return header.Parameter;
    }

    private static bool ConstantTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    // Test servers and some in-process hosts do not populate RemoteIpAddress. Treat that
    // transport as local only while the administrator token is deliberately disabled.
    private static bool IsLoopback(HttpContext context) => context.Connection.RemoteIpAddress is null ||
        context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}
