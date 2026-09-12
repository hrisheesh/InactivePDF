using InactivePDF.Application.Models;
using InactivePDF.Application;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;

namespace InactivePDF.UnitTests.Jobs;

public sealed class LiteDbJobStoreTests
{
    [Fact]
    public async Task JobAndPendingWorkSurviveStoreRecreation()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var job = CreateWorkItem();
            var accepted = new JobStatus(job.Job.Id, job.Job.CorrelationId, job.Job.Operation, ConversionJobState.Accepted, job.Job.AcceptedAt, job.Job.AcceptedAt, 0);
            using (var first = new LiteDbJobStore(Path.Combine(root, "state.db")))
            {
                Assert.Null(await first.CreateIfAbsentAsync(accepted, job));
                Assert.NotNull(await first.FindByCorrelationIdAsync(job.Job.CorrelationId));
            }

            using var second = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var pending = new List<ConversionWorkItem>();
            await foreach (var item in second.ReadPendingAsync()) pending.Add(item);
            Assert.Single(pending);
            Assert.Equal(job.Job.Id, pending[0].Job.Id);

            var succeeded = accepted with { State = ConversionJobState.Succeeded, UpdatedAt = DateTimeOffset.UtcNow, OutputPath = Path.Combine(root, "result.pdf") };
            await second.CompleteSuccessAsync(succeeded);
            Assert.Empty(await ToListAsync(second.ReadPendingAsync()));
            Assert.Equal(ConversionJobState.Succeeded, (await second.GetAsync(job.Job.Id))!.State);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ConcurrentCorrelationReuseReturnsTheOriginalJob()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var first = CreateWorkItem();
            var firstStatus = new JobStatus(first.Job.Id, first.Job.CorrelationId, first.Job.Operation, ConversionJobState.Accepted, first.Job.AcceptedAt, first.Job.AcceptedAt, 0);
            var secondJob = first with { Job = first.Job with { Id = Guid.NewGuid() } };
            var secondStatus = firstStatus with { JobId = secondJob.Job.Id };

            Assert.Null(await store.CreateIfAbsentAsync(firstStatus, first));
            var existing = await store.CreateIfAbsentAsync(secondStatus, secondJob);

            Assert.NotNull(existing);
            Assert.Equal(first.Job.Id, existing!.JobId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LeaseClaimRenewAndExpiryRecoveryAreAtomic()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var work = CreateWorkItem();
            var accepted = new JobStatus(work.Job.Id, work.Job.CorrelationId, work.Job.Operation, ConversionJobState.Accepted, work.Job.AcceptedAt, work.Job.AcceptedAt, 0);
            Assert.Null(await store.CreateIfAbsentAsync(accepted, work, "fingerprint-one"));

            var now = DateTimeOffset.UtcNow;
            var firstClaim = await store.ClaimPendingAsync("owner-one", 1, TimeSpan.FromMinutes(1), now);
            Assert.Single(firstClaim);
            Assert.Equal("owner-one", firstClaim[0].LeaseOwner);
            Assert.Empty(await store.ClaimPendingAsync("owner-two", 1, TimeSpan.FromMinutes(1), now));
            Assert.True(await store.RenewLeaseAsync(work.Job.Id, "owner-one", TimeSpan.FromMinutes(1), now));

            await store.ReleaseLeaseAsync(firstClaim[0], "owner-one", now);
            var secondClaim = await store.ClaimPendingAsync("owner-two", 1, TimeSpan.FromMinutes(1), now);
            Assert.Single(secondClaim);
            Assert.Equal("owner-two", secondClaim[0].LeaseOwner);

            await store.ReleaseLeaseAsync(secondClaim[0], "owner-two", now.AddMinutes(-2));
            var expiredClaim = await store.ClaimPendingAsync("owner-three", 1, TimeSpan.FromMinutes(1), now.AddMinutes(2));
            Assert.Single(expiredClaim);
            Assert.Equal("owner-three", expiredClaim[0].LeaseOwner);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ReusingCorrelationIdWithDifferentFingerprintIsRejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var first = CreateWorkItem();
            var status = new JobStatus(first.Job.Id, first.Job.CorrelationId, first.Job.Operation, ConversionJobState.Accepted, first.Job.AcceptedAt, first.Job.AcceptedAt, 0);
            Assert.Null(await store.CreateIfAbsentAsync(status, first, "fingerprint-one"));
            await store.UpsertAsync(status with { State = ConversionJobState.Running, Attempts = 1, UpdatedAt = DateTimeOffset.UtcNow });

            await Assert.ThrowsAsync<IdempotencyConflictException>(() => store.CreateIfAbsentAsync(
                status with { JobId = Guid.NewGuid() },
                first with { Job = first.Job with { Id = Guid.NewGuid() } },
                "fingerprint-two"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task JobOwnershipSurvivesPersistenceRoundTrip()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        var ownerApiKeyId = Guid.NewGuid();
        try
        {
            var work = CreateWorkItem();
            work = work with { Job = work.Job with { OwnerApiKeyId = ownerApiKeyId } };
            var status = new JobStatus(
                work.Job.Id,
                work.Job.CorrelationId,
                work.Job.Operation,
                ConversionJobState.Accepted,
                work.Job.AcceptedAt,
                work.Job.AcceptedAt,
                0,
                OwnerApiKeyId: ownerApiKeyId);

            using (var store = new LiteDbJobStore(databasePath))
            {
                Assert.Null(await store.CreateIfAbsentAsync(status, work, "fingerprint-owner"));
            }

            using var reopened = new LiteDbJobStore(databasePath);
            var restored = await reopened.GetAsync(work.Job.Id);
            Assert.Equal(ownerApiKeyId, restored!.OwnerApiKeyId);
            var claim = Assert.Single(await reopened.ClaimPendingAsync("worker", 1, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow));
            Assert.Equal(ownerApiKeyId, claim.Job.OwnerApiKeyId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task DeadLetterRetainsAttemptHistoryAndCanBeInspected()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var work = CreateWorkItem();
            var inputPath = Path.Combine(root, "input.txt");
            await File.WriteAllTextAsync(inputPath, "retryable input");
            work = work with { Inputs = [new StoredInput("input.txt", inputPath, new FileInfo(inputPath).Length)] };
            var accepted = new JobStatus(work.Job.Id, work.Job.CorrelationId, work.Job.Operation, ConversionJobState.Accepted, work.Job.AcceptedAt, work.Job.AcceptedAt, 0);
            Assert.Null(await store.CreateIfAbsentAsync(accepted, work, "fingerprint-history"));

            var firstClaim = Assert.Single(await store.ClaimPendingAsync("history-owner", 1, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow));
            var retryException = new ConversionWorkerExecutionException("temporary engine failure");
            await store.ScheduleRetryAsync(
                accepted with { State = ConversionJobState.Running, Attempts = 1, UpdatedAt = DateTimeOffset.UtcNow },
                firstClaim with { Attempt = 1 },
                "history-owner",
                DateTimeOffset.UtcNow,
                "worker_execution_failed",
                retryException.Message,
                retryException);

            var secondClaim = Assert.Single(await store.ClaimPendingAsync("history-owner", 1, TimeSpan.FromMinutes(1), DateTimeOffset.UtcNow));
            var finalException = new NotSupportedException("unsupported input with secret engine stderr and /Users/private/document.docx");
            await store.CompleteFailureWithLeaseAsync(
                accepted with { State = ConversionJobState.Failed, Attempts = 2, UpdatedAt = DateTimeOffset.UtcNow },
                secondClaim with { Attempt = 2 },
                finalException,
                "history-owner",
                "unsupported_format");

            var deadLetter = Assert.Single(await store.ListDeadLettersAsync(10));
            Assert.Equal(work.Job.Id, deadLetter.JobId);
            Assert.Equal(ConversionJobState.DeadLettered, (await store.GetAsync(work.Job.Id))!.State);
            Assert.Equal(2, deadLetter.AttemptHistory.Count);
            Assert.Equal("worker_execution_failed", deadLetter.AttemptHistory[0].ErrorCode);
            Assert.Equal("unsupported_format", deadLetter.AttemptHistory[1].ErrorCode);
            Assert.DoesNotContain("secret engine stderr", deadLetter.Exception, StringComparison.Ordinal);
            Assert.DoesNotContain("/Users/private", deadLetter.AttemptHistory[1].Exception, StringComparison.Ordinal);
            Assert.Equal(work.Job.Id, (await store.GetDeadLetterAsync(work.Job.Id))!.WorkItem.Job.Id);

            var retried = await store.RetryAsync(work.Job.Id, DateTimeOffset.UtcNow);
            Assert.Equal(ConversionJobState.Queued, retried!.State);
            Assert.Single(await ToListAsync(store.ReadPendingAsync()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ListsJobsWithStableCursorAndFiltersByLifecycleMetadata()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var first = CreateWorkItem();
            first = first with { Job = first.Job with { Id = Guid.NewGuid(), CorrelationId = "first" }, Request = first.Request with { CorrelationId = "first" } };
            var second = CreateWorkItem();
            second = second with { Job = second.Job with { Id = Guid.NewGuid(), CorrelationId = "second" }, Request = second.Request with { CorrelationId = "second" } };
            var firstStatus = new JobStatus(first.Job.Id, first.Job.CorrelationId, first.Job.Operation, ConversionJobState.Queued, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow, 0, Source: "API", Format: "docx");
            var secondStatus = new JobStatus(second.Job.Id, second.Job.CorrelationId, second.Job.Operation, ConversionJobState.Processing, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 1, Source: "WatchFolder", Format: "png");
            await store.CreateIfAbsentAsync(firstStatus, first, "first-fingerprint");
            await store.CreateIfAbsentAsync(secondStatus, second, "second-fingerprint");

            var page = await store.ListAsync(new JobQuery(Source: "API", Format: "docx", Limit: 1));

            var only = Assert.Single(page.Jobs);
            Assert.Equal(first.Job.Id, only.JobId);
            Assert.Null(page.NextCursor);

            var firstPage = await store.ListAsync(new JobQuery(Limit: 1));
            Assert.Single(firstPage.Jobs);
            Assert.NotNull(firstPage.NextCursor);
            var secondPage = await store.ListAsync(new JobQuery(Cursor: firstPage.NextCursor, Limit: 1));
            Assert.Single(secondPage.Jobs);
            Assert.NotEqual(firstPage.Jobs[0].JobId, secondPage.Jobs[0].JobId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CancellingQueuedJobRemovesPendingWorkAndDeletingTerminalJobRemovesStatus()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var work = CreateWorkItem();
            var status = new JobStatus(work.Job.Id, work.Job.CorrelationId, work.Job.Operation, ConversionJobState.Queued, work.Job.AcceptedAt, work.Job.AcceptedAt, 0);
            await store.CreateIfAbsentAsync(status, work, "cancel-fingerprint");

            var cancelled = await store.CancelAsync(work.Job.Id, DateTimeOffset.UtcNow);

            Assert.Equal(ConversionJobState.Cancelled, cancelled!.State);
            Assert.Empty(await ToListAsync(store.ReadPendingAsync()));
            Assert.NotNull(await store.DeleteAsync(work.Job.Id));
            Assert.Null(await store.GetAsync(work.Job.Id));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ExpiredIdempotencyRecordCanBeReusedWithoutChangingLiveRecords()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var first = CreateWorkItem();
            var expired = new JobStatus(first.Job.Id, first.Job.CorrelationId, first.Job.Operation, ConversionJobState.Succeeded, first.Job.AcceptedAt, first.Job.AcceptedAt, 1, IdempotencyExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
            await store.CreateIfAbsentAsync(expired, first, "expired-fingerprint");
            var replacement = first with { Job = first.Job with { Id = Guid.NewGuid() } };
            var replacementStatus = expired with { JobId = replacement.Job.Id, UpdatedAt = DateTimeOffset.UtcNow, IdempotencyExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };

            Assert.Null(await store.CreateIfAbsentAsync(replacementStatus, replacement, "replacement-fingerprint"));
            Assert.Equal(replacement.Job.Id, (await store.FindByCorrelationIdAsync(first.Job.CorrelationId))!.JobId);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task ProcessingJobsBecomeInterruptedAfterStoreReopen()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "state.db");
        try
        {
            var work = CreateWorkItem();
            var inputPath = Path.Combine(root, "input.txt");
            await File.WriteAllTextAsync(inputPath, "interrupted input");
            work = work with { Inputs = [new StoredInput("input.txt", inputPath, new FileInfo(inputPath).Length)] };
            var processing = new JobStatus(work.Job.Id, work.Job.CorrelationId, work.Job.Operation, ConversionJobState.Processing, work.Job.AcceptedAt, DateTimeOffset.UtcNow, 1);
            using (var store = new LiteDbJobStore(databasePath))
                await store.CreateIfAbsentAsync(processing, work, "interrupted-fingerprint");

            using var reopened = new LiteDbJobStore(databasePath);
            var restored = await reopened.GetAsync(work.Job.Id);
            Assert.Equal(ConversionJobState.Interrupted, restored!.State);
            Assert.Equal("service_restarted_before_completion", restored.ErrorCode);
            var retried = await reopened.RetryAsync(work.Job.Id, DateTimeOffset.UtcNow);
            Assert.Equal(ConversionJobState.Queued, retried!.State);
            Assert.Single(await ToListAsync(reopened.ReadPendingAsync()));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static ConversionWorkItem CreateWorkItem()
    {
        var job = new ConversionJob(Guid.NewGuid(), "correlation-" + Guid.NewGuid().ToString("N"), ConversionOperation.ConvertFile, ConversionJobState.Accepted, DateTimeOffset.UtcNow);
        var request = new ConversionRequest(job.CorrelationId, job.Operation, [new DocumentInput("input.txt", "text/plain", 4)], new ConversionOptions());
        return new ConversionWorkItem(job, request, [new StoredInput("input.txt", "/tmp/input.txt", 4)], "/tmp/job");
    }

    private static async Task<List<ConversionWorkItem>> ToListAsync(IAsyncEnumerable<ConversionWorkItem> source)
    {
        var result = new List<ConversionWorkItem>();
        await foreach (var item in source) result.Add(item);
        return result;
    }
}
