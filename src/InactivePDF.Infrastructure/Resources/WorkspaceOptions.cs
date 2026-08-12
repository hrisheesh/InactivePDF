namespace InactivePDF.Infrastructure.Resources;

public sealed class WorkspaceOptions
{
    public string RootPath { get; init; } = Path.Combine(Path.GetTempPath(), "InactivePDF", "jobs");
}
