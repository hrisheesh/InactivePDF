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
    public async Task DeadLetterRetainsAttemptHistoryAndCanBeInspected()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new LiteDbJobStore(Path.Combine(root, "state.db"));
            var work = CreateWorkItem();
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
            var finalException = new NotSupportedException("unsupported input");
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
            Assert.Equal(work.Job.Id, (await store.GetDeadLetterAsync(work.Job.Id))!.WorkItem.Job.Id);
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
