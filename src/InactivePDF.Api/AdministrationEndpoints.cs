using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using InactivePDF.Infrastructure.Jobs;
using System.Text.Json.Serialization;

namespace InactivePDF.Api;

public static class AdministrationEndpoints
{
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/v1/admin").AddEndpointFilter(async (context, next) =>
        {
            var request = context.HttpContext;
            var tokenConfigured = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INACTIVEPDF_API_TOKEN"));
            if (!tokenConfigured && (request.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
                return Results.Json(new { message = "Remote administration requires a configured API bearer token." }, statusCode: 403);
            // Fetch metadata prevents a foreign website from using a local unauthenticated instance.
            if (request.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                return Results.Json(new { message = "Cross-site administration is not allowed." }, statusCode: 403);
            if (request.Request.Query.TryGetValue("source", out var selectedSource) && selectedSource.ToString() is not ("" or "WatchFolder" or "Synchronous" or "Queued"))
                return Results.BadRequest(new { message = "Source must be WatchFolder, Synchronous, Queued, or empty for all sources." });
            return await next(context);
        });
        admin.MapGet("/settings", (AdministrationSettingsStore store) => Results.Ok(store.Read()));
        admin.MapGet("/performance-presets", () => Results.Ok(ServiceProfileStore.Presets()));
        admin.MapGet("/service-profiles", (ServiceProfileStore store) => Results.Ok(store.List()));
        admin.MapGet("/service-profiles/{name}", (string name, ServiceProfileStore store) =>
        {
            try { return store.Read(name) is { } profile ? Results.Ok(profile) : Results.NotFound(); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        });
        admin.MapPut("/service-profiles/{name}", (string name, JsonObject settings, ServiceProfileStore store) =>
        {
            try { store.Save(name, settings); return Results.NoContent(); }
            catch (Exception e) when (e is ArgumentException or InvalidDataException or JsonException) { return Results.BadRequest(new { message = e.Message }); }
        });
        admin.MapDelete("/service-profiles/{name}", (string name, ServiceProfileStore store) =>
        {
            try { return store.Delete(name) ? Results.NoContent() : Results.NotFound(); }
            catch (ArgumentException e) { return Results.BadRequest(new { message = e.Message }); }
        });
        admin.MapGet("/analytics", (int? hours, string? source, ConversionTelemetryStore store) => Results.Ok(store.Snapshot(hours ?? 24, source)));
        admin.MapGet("/logs", (string? q, AdministrationLiveService live) => Results.Ok(live.ReadLogs((q ?? "").Trim()[..Math.Min((q ?? "").Trim().Length, 128)])));
        admin.MapGet("/search", (string q, LiteDbJobStore jobs, ConversionTelemetryStore telemetry, AdministrationLiveService live) =>
        {
            if (q.Trim().Length is < 2 or > 128) return Results.BadRequest(new { message = "Search must contain 2–128 characters." });
            return Results.Ok(new { jobs = jobs.SearchJobs(q).Select(job => new { job.JobId, job.CorrelationId, job.State, job.ErrorCode }), executions = telemetry.Search(q), logs = live.ReadLogs(q.Trim()) });
        });
        admin.MapGet("/live", async (HttpContext context, string? source, AdministrationLiveService live) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
            try
            {
                do
                {
                    var snapshot = await live.SnapshotAsync(context.RequestAborted, source);
                    await context.Response.WriteAsync("event: snapshot\ndata: " + JsonSerializer.Serialize(snapshot, json) + "\n\n", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                } while (await timer.WaitForNextTickAsync(context.RequestAborted));
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        });
        admin.MapGet("/jobs", (int? limit, LiteDbJobStore store) => Results.Ok(store.ListRecent(limit ?? 50)
            .Select(job => new { job.JobId, job.CorrelationId, job.Operation, job.State, job.AcceptedAt, job.UpdatedAt, job.Attempts, job.ErrorCode, job.ErrorMessage })));
        admin.MapPut("/settings", (SettingsUpdate update, AdministrationSettingsStore store) =>
        {
            try { return Results.Ok(store.Save(update.Settings, update.Revision)); }
            catch (SettingsConflictException) { return Results.Conflict(new { message = "Settings changed since you opened this page. Reload before saving." }); }
            catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
            { return Results.BadRequest(new { message = error.Message }); }
            catch (UnauthorizedAccessException) { return Results.Json(new { message = "The service account cannot write the configured settings file." }, statusCode: 403); }
            catch (IOException) { return Results.Problem("Settings could not be saved. Check disk space and file permissions."); }
        });
        admin.MapGet("/status", async (HealthCheckService health, CancellationToken cancellationToken) =>
        {
            var report = await health.CheckHealthAsync(cancellationToken);
            using var process = Process.GetCurrentProcess();
            return Results.Ok(new
            {
                sampledAt = DateTimeOffset.UtcNow, uptimeSeconds = (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalSeconds,
                processMemoryBytes = process.WorkingSet64, cpuSeconds = process.TotalProcessorTime.TotalSeconds,
                logicalProcessors = Environment.ProcessorCount, platform = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                authenticationEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("INACTIVEPDF_API_TOKEN")),
                readiness = report.Status.ToString(), checks = report.Entries.Select(pair => new { name = pair.Key, status = pair.Value.Status.ToString(), description = pair.Value.Description })
            });
        });
    }

    public sealed record SettingsUpdate(JsonObject Settings, string Revision);
}
