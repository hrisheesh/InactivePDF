namespace InactivePDF.Domain.Models;

public sealed record JobWorkspace(
    Guid JobId,
    string RootPath,
    string InputPath,
    string OutputPath,
    string TemporaryPath,
    string ErrorPath);
