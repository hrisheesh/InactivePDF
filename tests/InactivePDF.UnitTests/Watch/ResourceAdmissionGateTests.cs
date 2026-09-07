using InactivePDF.Infrastructure.Jobs;

namespace InactivePDF.UnitTests.Watch;

public sealed class ResourceAdmissionGateTests
{
    [Fact]
    public async Task ReservationsCanRunTogetherWhenTheyFitTheBudget()
    {
        using var gate = new ResourceAdmissionGate(128L * 1024 * 1024, 2);
        await using var first = await gate.AcquireAsync(64L * 1024 * 1024);
        await using var second = await gate.AcquireAsync(64L * 1024 * 1024);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.AcquireAsync(64L * 1024 * 1024, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task ReleasedReservationAllowsTheNextJobToStart()
    {
        using var gate = new ResourceAdmissionGate(64L * 1024 * 1024, 1);
        var first = await gate.AcquireAsync(64L * 1024 * 1024);
        var waiting = gate.AcquireAsync(64L * 1024 * 1024).AsTask();

        await first.DisposeAsync();
        await using var second = await waiting;

        Assert.NotNull(second);
    }
}
