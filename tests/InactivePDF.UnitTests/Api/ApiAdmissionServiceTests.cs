using InactivePDF.Domain.Models;
using InactivePDF.Api;

namespace InactivePDF.UnitTests.Api;

public sealed class ApiAdmissionServiceTests
{
    [Fact]
    public void EnforcesRateAndConcurrentLimitsAtomically()
    {
        var root = CreateRoot();
        try
        {
            var keys = new ApiKeyStore(Path.Combine(root, "keys.json"));
            var created = keys.Create(new ApiKeyCreateRequest("limited", Limits: new ApiKeyLimits(RequestsPerMinute: 2, ConcurrentConversions: 1)), DateTimeOffset.UtcNow);
            var usage = new ApiUsageStore(Path.Combine(root, "usage.json"));
            var service = CreateService(root, keys, usage);
            var identity = new ApiIdentity(ApiIdentityKind.Integration, created.Key.Id, created.Key.Scopes.ToArray());
            var now = DateTimeOffset.UtcNow;

            Assert.Null(service.TryAdmit(identity, Request(), out var first, now));
            Assert.NotNull(first);
            var concurrent = service.TryAdmit(identity, Request(), out var second, now.AddSeconds(1));
            Assert.Equal("concurrent_conversions_exceeded", concurrent!.Code);
            Assert.Equal(429, concurrent.StatusCode);
            first!.Dispose();

            Assert.Null(service.TryAdmit(identity, Request(), out var third, now.AddSeconds(2)));
            third!.Dispose();
            var rate = service.TryAdmit(identity, Request(), out _, now.AddSeconds(3));
            Assert.Equal("requests_per_minute_exceeded", rate!.Code);
            Assert.Equal(429, rate.StatusCode);
            Assert.Equal(2, usage.Snapshot(created.Key.Id).RateLimitRejections);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void FailedAdmissionDoesNotConsumeSuccessfulInputQuota()
    {
        var root = CreateRoot();
        try
        {
            var keys = new ApiKeyStore(Path.Combine(root, "keys.json"));
            var created = keys.Create(new ApiKeyCreateRequest("quota", Limits: new ApiKeyLimits(DailyInputBytes: 10)), DateTimeOffset.UtcNow);
            var service = CreateService(root, keys);
            var identity = new ApiIdentity(ApiIdentityKind.Integration, created.Key.Id, created.Key.Scopes.ToArray());

            Assert.Null(service.TryAdmit(identity, Request(inputBytes: 8), out var failed, DateTimeOffset.UtcNow));
            failed!.Dispose();
            Assert.Null(service.TryAdmit(identity, Request(inputBytes: 10), out var successful, DateTimeOffset.UtcNow.AddSeconds(1)));
            successful!.CommitSuccess(10);

            var rejected = service.TryAdmit(identity, Request(inputBytes: 1), out _, DateTimeOffset.UtcNow.AddSeconds(2));
            Assert.Equal("daily_input_quota_exceeded", rejected!.Code);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task QueuedAdmissionIsReleasedWhenTheJobFinishes()
    {
        var root = CreateRoot();
        try
        {
            var keys = new ApiKeyStore(Path.Combine(root, "keys.json"));
            var created = keys.Create(new ApiKeyCreateRequest("queue", Limits: new ApiKeyLimits(MaximumQueuedJobs: 1, DailyInputBytes: 10)), DateTimeOffset.UtcNow);
            var service = CreateService(root, keys);
            var identity = new ApiIdentity(ApiIdentityKind.Integration, created.Key.Id, created.Key.Scopes.ToArray());
            var jobId = Guid.NewGuid();
            Assert.Null(service.TryAdmit(identity, Request(inputBytes: 5, queued: true), out var admission));
            admission!.CommitQueued(jobId);

            var blocked = service.TryAdmit(identity, Request(inputBytes: 1, queued: true), out _, DateTimeOffset.UtcNow.AddSeconds(1));
            Assert.Equal("queued_jobs_exceeded", blocked!.Code);
            using (var processing = await service.BeginQueuedProcessingAsync(jobId, identity.ApiKeyId)) processing.Complete(succeeded: true, actualInputBytes: 5);

            Assert.Null(service.TryAdmit(identity, Request(inputBytes: 5, queued: true), out var next));
            next!.Dispose();
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public void EnforcesPerKeyJobRetentionWithoutAffectingServerOwnedJobs()
    {
        var root = CreateRoot();
        try
        {
            var keys = new ApiKeyStore(Path.Combine(root, "keys.json"));
            var created = keys.Create(new ApiKeyCreateRequest("retained", Limits: new ApiKeyLimits(JobRetentionDays: 1)), DateTimeOffset.UtcNow);
            var service = CreateService(root, keys);
            var acceptedAt = DateTimeOffset.UtcNow;

            Assert.False(service.IsJobExpired(created.Key.Id, acceptedAt, acceptedAt.AddHours(23)));
            Assert.True(service.IsJobExpired(created.Key.Id, acceptedAt, acceptedAt.AddDays(1)));
            Assert.False(service.IsJobExpired(null, acceptedAt, acceptedAt.AddYears(10)));
        }
        finally { DeleteRoot(root); }
    }

    private static ApiAdmissionRequest Request(long inputBytes = 1, bool queued = false) => new(inputBytes, inputBytes, inputBytes, 1, queued);

    private static ApiAdmissionService CreateService(string root, ApiKeyStore keys, ApiUsageStore? usage = null) => new(
        keys,
        new ApiRequestLimits(1_000, 1_000, 10),
        new ResourcePolicy(1_000, 1_000, 1, TimeSpan.FromMinutes(1)),
        Path.Combine(root, "quota.json"), usage);

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "inactivepdf-admission-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}
