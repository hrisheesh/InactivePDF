using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.Api;

/// <summary>
/// Streams a file-backed conversion result and releases its workspace after the response ends.
/// </summary>
internal sealed class ConversionFileResult(IsolatedConversionOutput output) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "application/pdf";
        httpContext.Response.ContentLength = output.Length;
        httpContext.Response.Headers.ContentDisposition = $"attachment; filename=\"{output.DownloadName}\"";
        try
        {
            await output.Content.CopyToAsync(httpContext.Response.Body, 64 * 1024, httpContext.RequestAborted).ConfigureAwait(false);
        }
        finally
        {
            await output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
