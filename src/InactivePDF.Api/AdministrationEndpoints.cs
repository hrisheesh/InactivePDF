using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Domain.Contracts;
using System.Text.Json.Serialization;

namespace InactivePDF.Api;

public static class AdministrationEndpoints
{
    public static void Map(WebApplication app)
    {
        var admin = app.MapGroup("/v1/admin").RequireAdministrator().AddEndpointFilter(async (context, next) =>
        {
            // Fetch metadata prevents a foreign website from using a local unauthenticated instance.
            var request = context.HttpContext;
            if (request.Request.Headers["Sec-Fetch-Site"] == "cross-site")
                return Results.Json(new { message = "Cross-site administration is not allowed." }, statusCode: 403);
            if (request.Request.Query.TryGetValue("source", out var selectedSource) && selectedSource.ToString() is not ("" or "WatchFolder" or "Synchronous" or "Queued"))
                return Results.BadRequest(new { message = "Source must be WatchFolder, Synchronous, Queued, or empty for all sources." });
            return await next(context);
        });
        admin.MapPost("/api-keys", (ApiKeyCreateRequest request, ApiKeyStore store, IClock clock) =>
        {
            try
            {
                var created = store.Create(request, clock.UtcNow);
                return Results.Created($"/v1/admin/api-keys/{created.Key.Id:D}", created);
            }
            catch (ApiKeyNameConflictException error) { return Results.Conflict(new { code = "api_key_name_exists", message = error.Message }); }
            catch (ArgumentException error) { return Results.BadRequest(new { code = "invalid_api_key", message = error.Message }); }
        });
        admin.MapGet("/api-keys", (ApiKeyStore store) => Results.Ok(store.List()));
        admin.MapGet("/api-keys/{id:guid}", (Guid id, ApiKeyStore store) => store.Get(id) is { } key ? Results.Ok(key) : Results.NotFound());
        admin.MapPost("/api-keys/{id:guid}/rotate", (Guid id, ApiKeyRotateRequest? request, ApiKeyStore store, IClock clock) =>
        {
            try
            {
                var rotated = store.Rotate(id, request, clock.UtcNow);
                return rotated is null ? Results.NotFound() : Results.Ok(rotated);
            }
            catch (ArgumentException error) { return Results.BadRequest(new { code = "invalid_api_key", message = error.Message }); }
        });
        admin.MapPost("/api-keys/{id:guid}/revoke", (Guid id, ApiKeyStore store, IClock clock) =>
            store.Revoke(id, clock.UtcNow) is { } key ? Results.Ok(key) : Results.NotFound());
        admin.MapPut("/api-keys/{id:guid}/limits", (Guid id, ApiKeyLimits limits, ApiKeyStore store) =>
        {
            try { return store.UpdateLimits(id, limits) is { } key ? Results.Ok(key) : Results.NotFound(); }
            catch (ArgumentException error) { return Results.BadRequest(new { code = "invalid_api_key_limits", message = error.Message }); }
        });
        admin.MapGet("/api-keys/{id:guid}/usage", (Guid id, ApiKeyStore keys, ApiUsageStore usage) =>
            keys.Get(id) is null ? Results.NotFound() : Results.Ok(usage.Snapshot(id)));
        admin.MapGet("/usage", (ApiKeyStore keys, ApiUsageStore usage) =>
        {
            var items = usage.SnapshotAll(keys.List());
            return Results.Ok(new { sampledAt = DateTimeOffset.UtcNow, items, totals = UsageTotals(items) });
        });
        admin.MapGet("/audit-events", (int? limit, AuditEventStore audit) => Results.Ok(new
        {
            sampledAt = DateTimeOffset.UtcNow,
            events = audit.Recent(Math.Clamp(limit ?? 100, 1, 1_000)),
            recordingError = audit.RecordingError
        }));
        admin.MapDelete("/api-keys/{id:guid}", (Guid id, ApiKeyStore store) => store.Delete(id) ? Results.NoContent() : Results.NotFound());
        admin.MapGet("/settings", (AdministrationSettingsStore store) => Results.Ok(store.Read()));
        admin.MapGet("/performance-presets", () => Results.Ok(ServiceProfileStore.Presets()));
        admin.MapPost("/profiles", (ProfileWriteRequest request, WatermarkProfileStore store) => ProfileAdministration.Create(request, store));
        admin.MapPut("/profiles/{name}", (string name, ProfileWriteRequest request, WatermarkProfileStore store) => ProfileAdministration.Update(name, request, store));
        admin.MapDelete("/profiles/{name}", (string name, string? type, WatermarkProfileStore store) => ProfileAdministration.Delete(name, type, store));
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
        admin.MapGet("/search", (string q, int? limit, AdministrationSearchService search) =>
        {
            try { return Results.Ok(search.Search(q, limit ?? 60)); }
            catch (ArgumentOutOfRangeException error) { return Results.BadRequest(new { message = error.Message }); }
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
        admin.MapGet("/jobs", (int? limit, LiteDbJobStore store) => Results.Ok(store.ListRecent(limit ?? 50).Select(JobApiResponses.ToPublic)));
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

    private static object UsageTotals(IReadOnlyList<ApiKeyUsageSnapshot> items) => new
    {
        totalRequests = items.Sum(item => item.TotalRequests),
        successfulConversions = items.Sum(item => item.SuccessfulConversions),
        failedConversions = items.Sum(item => item.FailedConversions),
        bytesReceived = items.Sum(item => item.BytesReceived),
        bytesProduced = items.Sum(item => item.BytesProduced),
        bytesSaved = items.Sum(item => item.BytesSaved),
        rateLimitRejections = items.Sum(item => item.RateLimitRejections),
        retries = items.Sum(item => item.Retries)
    };

    public sealed record SettingsUpdate(JsonObject Settings, string Revision);
}
