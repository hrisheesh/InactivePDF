namespace InactivePDF.Api;

public static class ApiAuthorizationExtensions
{
    public static RouteGroupBuilder RequireAdministrator(this RouteGroupBuilder builder) =>
        builder.AddEndpointFilter(Authorize(ApiPolicyEvaluator.EvaluateAdministrator, "Administrator authentication is required."));

    public static RouteHandlerBuilder RequireAdministrator(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(Authorize(ApiPolicyEvaluator.EvaluateAdministrator, "Administrator authentication is required."));

    public static RouteHandlerBuilder RequireScope(this RouteHandlerBuilder builder, string scope) =>
        builder.AddEndpointFilter(Authorize(identity => ApiPolicyEvaluator.EvaluateScope(identity, scope), $"The '{scope}' scope is required."));

    private static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Authorize(
        Func<ApiIdentity?, ApiPolicyDecision> evaluate,
        string forbiddenMessage) => async (context, next) =>
    {
        var decision = evaluate(ApiAuthentication.GetIdentity(context.HttpContext));
        return decision is ApiPolicyDecision.Allow ? await next(context) : ApiPolicyEvaluator.ToResult(decision, forbiddenMessage);
    };
}
