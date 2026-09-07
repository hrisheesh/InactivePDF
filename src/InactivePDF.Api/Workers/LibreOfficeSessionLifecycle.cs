using InactivePDF.Infrastructure.Processes;
using Microsoft.Extensions.Hosting;

namespace InactivePDF.Api.Workers;

internal sealed class LibreOfficeSessionLifecycle(LibreOfficeSessionHost session) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => session.StopAsync(cancellationToken);
}
