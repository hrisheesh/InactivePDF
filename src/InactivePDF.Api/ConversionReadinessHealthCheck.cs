using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Resources;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace InactivePDF.Api;

public sealed class ConversionReadinessHealthCheck(WorkspaceOptions workspaceOptions) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            Directory.CreateDirectory(workspaceOptions.RootPath);
            var probe = Path.Combine(workspaceOptions.RootPath, $".readiness-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);

            var libreOfficePath = LibreOfficeOptions.Default.ExecutablePath;
            var executableAvailable = IsExecutableAvailable(libreOfficePath);
            return executableAvailable && IsolatedConversionWorker.IsWorkerAvailable
                ? Task.FromResult(HealthCheckResult.Healthy("Data storage and configured conversion engine are available."))
                : Task.FromResult(HealthCheckResult.Unhealthy($"LibreOffice or the isolated conversion worker is unavailable. LibreOffice='{libreOfficePath}'."));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("InactivePDF data storage is not writable.", exception));
        }
    }

    private static bool IsExecutableAvailable(string executable)
    {
        if (Path.IsPathFullyQualified(executable) || executable.Contains(Path.DirectorySeparatorChar))
            return File.Exists(executable);

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, executable)) ||
                OperatingSystem.IsWindows() && File.Exists(Path.Combine(directory, executable + ".exe")));
    }
}
