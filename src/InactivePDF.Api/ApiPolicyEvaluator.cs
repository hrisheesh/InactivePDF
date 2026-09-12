namespace InactivePDF.Api;

public enum ApiPolicyDecision
{
    Allow,
    Unauthenticated,
    Forbidden,
    NotFound
}

/// <summary>Centralizes scope, identity, and resource-ownership decisions.</summary>
public static class ApiPolicyEvaluator
{
    public static ApiPolicyDecision EvaluateAdministrator(ApiIdentity? identity) =>
        identity is null ? ApiPolicyDecision.Unauthenticated :
        identity.IsAdministrator ? ApiPolicyDecision.Allow : ApiPolicyDecision.Forbidden;

    public static ApiPolicyDecision EvaluateScope(ApiIdentity? identity, string requiredScope) =>
        identity is null ? ApiPolicyDecision.Unauthenticated :
        identity.HasScope(requiredScope) ? ApiPolicyDecision.Allow : ApiPolicyDecision.Forbidden;

    public static ApiPolicyDecision EvaluateOwnedResource(ApiIdentity? identity, string requiredScope, Guid? ownerApiKeyId)
    {
        var scopeDecision = EvaluateScope(identity, requiredScope);
        if (scopeDecision is not ApiPolicyDecision.Allow)
            return scopeDecision;

        return identity!.Kind == ApiIdentityKind.Integration && identity.ApiKeyId != ownerApiKeyId
            ? ApiPolicyDecision.NotFound
            : ApiPolicyDecision.Allow;
    }

    public static IResult ToResult(ApiPolicyDecision decision, string? forbiddenMessage = null) => decision switch
    {
        ApiPolicyDecision.Unauthenticated => Results.Json(new { code = "unauthorized", message = "A valid bearer token is required." }, statusCode: StatusCodes.Status401Unauthorized),
        ApiPolicyDecision.Forbidden => Results.Json(new { code = "forbidden", message = forbiddenMessage ?? "The credential does not have permission for this operation." }, statusCode: StatusCodes.Status403Forbidden),
        ApiPolicyDecision.NotFound => Results.NotFound(),
        _ => throw new ArgumentOutOfRangeException(nameof(decision), decision, "An allow decision has no error response.")
    };
}
