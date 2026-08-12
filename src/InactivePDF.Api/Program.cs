using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Validation;
using InactivePDF.Application.Capabilities;
using InactivePDF.Domain.Contracts;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Configuration;
using InactivePDF.Infrastructure.IO;
using InactivePDF.Infrastructure.Policies;
using InactivePDF.Infrastructure.Processes;
using InactivePDF.Infrastructure.Resources;
using InactivePDF.Infrastructure.Rendering;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Watch;
using InactivePDF.Application.Resources;
using InactivePDF.Api.Workers;
using InactivePDF.Api;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

InactivePdfSettings.LoadAndApply();

if (ConversionWorkerEntryPoint.IsWorker(args))
{
    Environment.ExitCode = await ConversionWorkerEntryPoint.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IConversionJobIdGenerator, GuidConversionJobIdGenerator>();
builder.Services.AddSingleton<IConversionRequestValidator, ConversionRequestValidator>();
builder.Services.AddSingleton<IConversionJobCoordinator, ConversionJobCoordinator>();
builder.Services.AddSingleton<IBoundedStreamCopier, BoundedStreamCopier>();
builder.Services.AddSingleton<IAtomicFilePublisher, AtomicFilePublisher>();
builder.Services.AddSingleton<IDiskSpaceGuard, DriveInfoDiskSpaceGuard>();
builder.Services.AddSingleton<IProcessTreeController, ProcessTreeController>();
var dataRoot = ResolveDataRoot();
var stateRoot = ResolveChildPath("INACTIVEPDF_STATE_PATH", Path.Combine(dataRoot, "state"));
var jobRoot = ResolveChildPath("INACTIVEPDF_JOBS_PATH", Path.Combine(dataRoot, "jobs"));
var requestLimits = ApiRequestLimits.FromEnvironment();
builder.Services.AddSingleton(requestLimits);
builder.Services.AddSingleton(ResourcePolicy.FromEnvironment());
builder.Services.AddSingleton<ConversionWorkerOptions>(_ => ConversionWorkerOptions.FromEnvironment());
builder.Services.AddSingleton(ConversionConcurrencyOptions.FromEnvironment());
builder.Services.AddSingleton<ConversionConcurrencyGate>();
builder.Services.AddSingleton<LiteDbJobStore>(_ => new LiteDbJobStore(Path.Combine(stateRoot, "inactivepdf.db")));
builder.Services.AddSingleton<IJobPersistence>(services => services.GetRequiredService<LiteDbJobStore>());
builder.Services.AddSingleton<IJobStatusStore>(services => services.GetRequiredService<LiteDbJobStore>());
builder.Services.AddSingleton(new WorkspaceOptions { RootPath = jobRoot });
builder.Services.AddSingleton<IJobWorkspaceFactory, FileSystemJobWorkspaceFactory>();
builder.Services.AddSingleton<JobWorkspaceService>();
builder.Services.AddSingleton<IsolatedConversionWorker>();
builder.Services.AddSingleton<IsolatedConversionService>();
builder.Services.AddSingleton<IConversionWorkProcessor, IsolatedConversionWorkProcessor>();
builder.Services.AddSingleton<ConversionMetrics>();
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = requestLimits.MaximumRequestBytes);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = requestLimits.MaximumRequestBytes);
builder.Services.AddSingleton<IConversionJobBuffer>(services => new ConversionJobBuffer(services.GetRequiredService<ConversionWorkerOptions>().QueueCapacity));
builder.Services.AddHostedService<ConversionWorker>();
builder.Services.AddSingleton(WatchFolderOptions.FromEnvironment());
builder.Services.AddHostedService<WatchFolderWorker>();
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(ParsePositiveEnvironment("INACTIVEPDF_SHUTDOWN_TIMEOUT_SECONDS", 30)));
builder.Services.AddHealthChecks()
    .AddCheck<ConversionReadinessHealthCheck>("conversion-readiness", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    context.Response.ContentType = "application/json";
    context.Response.StatusCode = exception is ConversionFormatException
        ? StatusCodes.Status422UnprocessableEntity
        : StatusCodes.Status500InternalServerError;
    var code = exception is ConversionFormatException format ? format.Code : "conversion_failed";
    await context.Response.WriteAsJsonAsync(new { code, message = exception?.Message ?? "The conversion failed." });
}));

app.MapHealthChecks("/health");
app.MapHealthChecks("/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready", StringComparer.OrdinalIgnoreCase) });
JobEndpoints.Map(app);
CompatibilityEndpoints.Map(app);
app.MapGet("/v1/capabilities", () => Results.Ok(new
{
    service = "InactivePDF",
    version = "0.1.0-beta",
    runtime = ".NET 10",
    platform = "Windows Server 2016+",
    operations = ApiCapabilities.Operations,
    formats = SupportedFormatCatalog.All.Select(format => new
    {
        format.Extension,
        category = format.Category.ToString(),
        route = format.Route.ToString(),
        format.ContentTypes,
        format.SupportsVisualLayout,
        format.SupportsLosslessPassThrough
    }),
    profiles = PdfOutputProfileCatalog.All.Select(profile => new
    {
        profile.Name,
        profile.PdfVersion,
        profile.PreserveJpegData,
        profile.DownsampleImages,
        profile.MaximumImageDpi,
        profile.JpegQuality,
        profile.CompressContentStreams,
        profile.BestCompression,
        profile.PreserveSourceMetadata,
        profile.FontPolicy,
        profile.EncryptionPolicy,
        profile.LargeMarkupPolicy,
        profile.StructuralValidation
    })
}));
app.MapGet("/v1/metrics", (ConversionMetrics metrics, IConversionJobBuffer queue) => Results.Ok(new
{
    metrics.Accepted,
    metrics.Succeeded,
    metrics.Failed,
    metrics.Retried,
    metrics.DeadLettered,
    queueDepth = queue.Count,
    queueCapacity = queue.Capacity
}));

app.Run();

static string ResolveDataRoot() =>
    Environment.GetEnvironmentVariable("INACTIVEPDF_DATA_PATH") is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "InactivePDF")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "InactivePDF");

static int ParsePositiveEnvironment(string name, int fallback) =>
    int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

static string ResolveChildPath(string name, string fallback) =>
    Environment.GetEnvironmentVariable(name) is { Length: > 0 } configured
        ? Path.GetFullPath(configured)
        : fallback;

public partial class Program;

internal static class ApiCapabilities
{
    public static readonly string[] Operations =
    [ "convert-file", "convert-files", "convert-and-merge", "create-text-pdf" ];
}
