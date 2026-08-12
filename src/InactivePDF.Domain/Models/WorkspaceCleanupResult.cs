namespace InactivePDF.Domain.Models;

public sealed record WorkspaceCleanupResult(
    bool Succeeded,
    string WorkspacePath,
    string? ErrorMessage);
