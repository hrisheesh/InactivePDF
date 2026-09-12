using InactivePDF.Api;

namespace InactivePDF.UnitTests.Api;

public sealed class ApiPolicyEvaluatorTests
{
    [Fact]
    public void AppliesIdentityScopeAndOwnershipInOrder()
    {
        var keyId = Guid.NewGuid();
        var otherKeyId = Guid.NewGuid();
        var owner = new ApiIdentity(ApiIdentityKind.Integration, keyId, [ApiKeyScopes.JobsRead]);
        var administrator = new ApiIdentity(ApiIdentityKind.Administrator, null, []);

        Assert.Equal(ApiPolicyDecision.Unauthenticated, ApiPolicyEvaluator.EvaluateScope(null, ApiKeyScopes.JobsRead));
        Assert.Equal(ApiPolicyDecision.Forbidden, ApiPolicyEvaluator.EvaluateScope(owner, ApiKeyScopes.OutputsRead));
        Assert.Equal(ApiPolicyDecision.Allow, ApiPolicyEvaluator.EvaluateOwnedResource(owner, ApiKeyScopes.JobsRead, keyId));
        Assert.Equal(ApiPolicyDecision.NotFound, ApiPolicyEvaluator.EvaluateOwnedResource(owner, ApiKeyScopes.JobsRead, otherKeyId));
        Assert.Equal(ApiPolicyDecision.NotFound, ApiPolicyEvaluator.EvaluateOwnedResource(owner, ApiKeyScopes.JobsRead, null));
        Assert.Equal(ApiPolicyDecision.Allow, ApiPolicyEvaluator.EvaluateOwnedResource(administrator, ApiKeyScopes.JobsRead, otherKeyId));
    }
}
