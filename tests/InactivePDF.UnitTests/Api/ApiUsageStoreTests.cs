using System.Text.Json;
using InactivePDF.Api;
using Microsoft.AspNetCore.Http;

namespace InactivePDF.UnitTests.Api;

public sealed class ApiUsageStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inactivepdf-api-usage-" + Guid.NewGuid().ToString("N"));

    public ApiUsageStoreTests() => Directory.CreateDirectory(root);

    [Fact]
    public void AggregatesConversionsRejectionsFormatsAndPersistsAtomically()
    {
        var path = Path.Combine(root, "usage.json");
        var key = Guid.NewGuid();
        using (var store = new UsageScope(path))
        {
            var now = DateTimeOffset.UtcNow;
            store.Value.RecordAccepted(key, ".docx", now);
            store.Value.RecordCompleted(key, "docx", true, 100, 60, 25, 4, 12, 2048, now.AddMilliseconds(25));
            store.Value.RecordAccepted(key, "docx", now.AddSeconds(1));
            store.Value.RecordCompleted(key, "docx", false, 100, 0, null, 8, completedAt: now.AddSeconds(1));
            store.Value.RecordRetry(key);
            store.Value.RecordRateLimitRejection(key, "requests_per_minute_exceeded", now.AddSeconds(2));
        }

        using var restored = new UsageScope(path);
        var snapshot = restored.Value.Snapshot(key);
        Assert.Equal(2, snapshot.TotalRequests);
        Assert.Equal(1, snapshot.SuccessfulConversions);
        Assert.Equal(1, snapshot.FailedConversions);
        Assert.Equal(200, snapshot.BytesReceived);
        Assert.Equal(60, snapshot.BytesProduced);
        Assert.Equal(40, snapshot.BytesSaved);
        Assert.Equal(40, snapshot.SavingsPercent);
        Assert.Equal(25, snapshot.Conversion.AverageMilliseconds);
        Assert.Equal(6, snapshot.QueueWait.AverageMilliseconds);
        Assert.Equal(1, snapshot.Retries);
        Assert.Equal(1, snapshot.RateLimitRejections);
        Assert.Equal(1, snapshot.Formats.Single().Successful);
        Assert.DoesNotContain("secret", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void UnknownKeyHasAnEmptySnapshotWithoutCreatingAFile()
    {
        var path = Path.Combine(root, "usage.json");
        using var store = new UsageScope(path);
        var snapshot = store.Value.Snapshot(Guid.NewGuid());
        Assert.Equal(0, snapshot.TotalRequests);
        Assert.Null(snapshot.Conversion.AverageMilliseconds);
        Assert.Empty(snapshot.Formats);
        Assert.False(File.Exists(path));
    }

    public void Dispose() => Directory.Delete(root, true);

    private sealed class UsageScope : IDisposable
    {
        public ApiUsageStore Value { get; }
        public UsageScope(string path) => Value = new ApiUsageStore(path);
        public void Dispose() { }
    }
}

public sealed class AuditEventStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "inactivepdf-audit-" + Guid.NewGuid().ToString("N"));

    public AuditEventStoreTests() => Directory.CreateDirectory(root);

    [Fact]
    public void RecordsSafeRequestMetadataAndReloadsIt()
    {
        var path = Path.Combine(root, "audit.jsonl");
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/v1/admin/api-keys";
        context.Response.StatusCode = StatusCodes.Status201Created;
        context.TraceIdentifier = "request-1";
        context.Items["InactivePDF.Api.Identity"] = new ApiIdentity(ApiIdentityKind.Administrator, null, []);

        var store = new AuditEventStore(path);
        store.Record(context);
        Assert.Single(store.Recent());
        Assert.Equal("POST", store.Recent()[0].Method);
        Assert.Equal("/v1/admin/api-keys", store.Recent()[0].Path);
        Assert.DoesNotContain("Bearer", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);

        var restored = new AuditEventStore(path);
        Assert.Equal("request-1", Assert.Single(restored.Recent()).RequestId);
        Assert.NotEqual(Guid.Empty, Assert.Single(restored.Recent()).Id);
    }

    [Fact]
    public void DoesNotRecordLiveFeedOrSuccessfulReadOnlyPolling()
    {
        var path = Path.Combine(root, "audit.jsonl");
        var store = new AuditEventStore(path);
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/v1/admin/live";
        context.Response.StatusCode = StatusCodes.Status200OK;
        store.Record(context);
        Assert.Empty(store.Recent());
        Assert.False(File.Exists(path));
    }

    public void Dispose() => Directory.Delete(root, true);
}
