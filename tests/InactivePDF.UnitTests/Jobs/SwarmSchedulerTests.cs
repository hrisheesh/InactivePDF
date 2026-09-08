using System.Collections.Concurrent;
using System.Text.Json;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.UnitTests.Jobs;

public sealed class SwarmSchedulerTests
{
    private static ConversionWorkerRequest Request(string name, string source = "Queued") => new(ConversionOperation.ConvertFile, "unused.pdf", [new(name, name)], Source: source, JobId: name);

    [Theory]
    [InlineData(20)]
    [InlineData(200)]
    [InlineData(1000)]
    [InlineData(2000)]
    public async Task DispatchesEveryItemExactlyOnceWithinParallelLimit(int count)
    {
        var scheduler = new SwarmScheduler(new SwarmOptions(MaximumParallelWorkers: 4, TextSlots: 4));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var seen = new ConcurrentDictionary<int, int>();
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            using var lease = await scheduler.AcquireAsync(Request(i + ".txt"), timeout.Token);
            Assert.True(seen.TryAdd(i, 1));
            await Task.Delay(1, timeout.Token);
        }).ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(count, seen.Count);
        var snapshot = JsonSerializer.SerializeToElement(scheduler.Snapshot());
        Assert.Equal(count, snapshot.GetProperty("completed").GetInt64());
        Assert.Equal(4, snapshot.GetProperty("peakActive").GetInt32());
        Assert.Equal(0, snapshot.GetProperty("reservedBytes").GetInt64());
        Assert.Equal((0, 0), scheduler.Counts());
    }

    [Fact]
    public async Task BlockedOfficeLaneDoesNotBlockTextAndCancellationReclaimsQueue()
    {
        var scheduler = new SwarmScheduler(new SwarmOptions(MaximumParallelWorkers: 3, TextSlots: 2));
        using var first = await scheduler.AcquireAsync(Request("one.docx"));
        using var cancellation = new CancellationTokenSource();
        var blocked = scheduler.AcquireAsync(Request("two.docx"), cancellation.Token);
        Assert.False(blocked.IsCompleted);
        using var text = await scheduler.AcquireAsync(Request("three.txt", "WatchFolder"));
        Assert.Equal((0, 1), scheduler.Counts("WatchFolder"));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
        first.Dispose();text.Dispose();
        Assert.Equal((0, 0), scheduler.Counts());
    }

    [Fact]
    public async Task MemoryAndPendingLimitsAreEnforcedAndFailureReleasesCapacity()
    {
        var scheduler = new SwarmScheduler(new SwarmOptions(MaximumParallelWorkers: 4, MaximumPending: 1, ImageSlots: 4, MemoryBudgetBytes: 1073741824));
        using var first = await scheduler.AcquireAsync(Request("one.png"));
        var second = scheduler.AcquireAsync(Request("two.png"));
        Assert.False(second.IsCompleted);
        await Assert.ThrowsAsync<SwarmCapacityException>(() => scheduler.AcquireAsync(Request("three.png")));
        first.Error = new IOException();first.Dispose();
        using var lease = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal((0, 1), scheduler.Counts());
        lease.Dispose();
        var snapshot = JsonSerializer.SerializeToElement(scheduler.Snapshot());
        Assert.Equal(1, snapshot.GetProperty("failed").GetInt64());
        Assert.Equal(1, snapshot.GetProperty("completed").GetInt64());
        Assert.Equal(0, snapshot.GetProperty("reservedBytes").GetInt64());
    }

    [Fact]
    public async Task RepeatedWeightedAdmissionFinishesWithoutPartialReservationDeadlock()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var gate = new ResourceAdmissionGate(256L * 1024 * 1024, 8);
        var tasks = Enumerable.Range(0, 40).Select(async _ =>
        {
            await using var lease = await gate.AcquireAsync(192L * 1024 * 1024, cancellation.Token);
            await Task.Delay(1, cancellation.Token);
        });
        await Task.WhenAll(tasks);
    }
}
