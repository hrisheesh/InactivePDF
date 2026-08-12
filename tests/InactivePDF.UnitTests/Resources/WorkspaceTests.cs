using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.UnitTests.Resources;

public sealed class WorkspaceTests
{
    [Fact]
    public async Task WorkspaceIsolatedByJobAndRemovedOnceDisposed()
    {
        var root = Path.Combine(Path.GetTempPath(), "InactivePDF-tests", Guid.NewGuid().ToString("N"));
        var factory = new FileSystemJobWorkspaceFactory(new WorkspaceOptions { RootPath = root });
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();

        await using var first = await factory.CreateAsync(firstId);
        await using var second = await factory.CreateAsync(secondId);

        Assert.NotEqual(first.Workspace.RootPath, second.Workspace.RootPath);
        Assert.True(Directory.Exists(first.Workspace.InputPath));
        Assert.True(Directory.Exists(second.Workspace.InputPath));

        var cleanup = await first.CleanupAsync();

        Assert.True(cleanup.Succeeded);
        Assert.False(Directory.Exists(first.Workspace.RootPath));
        Assert.True(Directory.Exists(second.Workspace.RootPath));
    }

    [Fact]
    public async Task WorkspaceFactoryRejectsEmptyJobId()
    {
        var factory = new FileSystemJobWorkspaceFactory(new WorkspaceOptions());

        await Assert.ThrowsAsync<ArgumentException>(async () => await factory.CreateAsync(Guid.Empty));
    }
}
