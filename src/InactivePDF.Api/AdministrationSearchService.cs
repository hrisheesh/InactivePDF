using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Jobs;
using InactivePDF.Infrastructure.Watch;

namespace InactivePDF.Api;

public sealed class AdministrationSearchService(
    AdministrationSettingsStore settings,
    ServiceProfileStore serviceProfiles,
    WatermarkProfileStore watermarkProfiles,
    LiteDbJobStore jobs,
    ConversionTelemetryStore telemetry,
    AdministrationLiveService live,
    WatchFolderOptions watch)
{
    private const int MaxQueryLength = 128;
    private const int MaxResults = 60;
    private static readonly (string File, string Summary)[] Documentation =
    [
        ("README.md", "Product setup, API, workers, queue, authentication, and configuration"),
        ("ARCHITECTURE.md", "Service architecture, workers, queue, and conversion pipeline"),
        ("PRD.md", "Product requirements, supported formats, and operations"),
        ("WATCHFOLDER-STATUS.md", "Watch-folder processing, recovery, and queue status"),
        ("KNOWN_ISSUES.md", "Known issues, failures, and operational limitations"),
        ("ACTION_PLAN.md", "Release actions, security, testing, and performance"),
        ("PHASES.md", "Development phases, workers, settings, and dashboard")
    ];

    public object Search(string query, int limit = MaxResults)
    {
        var normalized = query.Trim();
        if (normalized.Length is < 2 or > MaxQueryLength)
            throw new ArgumentOutOfRangeException(nameof(query), "Search must contain 2–128 characters.");

        var terms = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.ToLowerInvariant()).ToArray();
        var results = new List<SearchResult>();

        AddSettings(results);
        AddServiceProfiles(results);
        AddWatermarkProfiles(results);
        AddWatchFolder(results);
        AddDocumentation(results);

        var jobMatches = jobs.SearchJobs(normalized);
        results.AddRange(jobMatches.Select(job => new SearchResult(
            "job", job.JobId.ToString(), $"{job.State} · {job.Operation}", "jobs", job.JobId.ToString())));

        var executionMatches = telemetry.Search(normalized);
        results.AddRange(executionMatches.Select(row => new SearchResult(
            "conversion", row.FileName, $"{row.Source} · {row.State} · {FormatDuration(row.DurationMs)}", "jobs", row.Id)));

        var logMatches = live.ReadLogs(normalized);
        var logRows = JsonSerializer.SerializeToNode(logMatches)?["rows"]?.AsArray() ?? [];
        results.AddRange(logRows.Select(row => new SearchResult(
            "log", SafeFileName(row?["fileName"]?.GetValue<string>()),
            $"{row?["event"]?.GetValue<string>()} · {row?["utc"]?.GetValue<string>()}", "jobs", row?["utc"]?.GetValue<string>() ?? "logs")));

        var unique = results
            .Where(result => Matches(result, terms))
            .GroupBy(result => result.Kind + "|" + result.Title + "|" + result.Target, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(Math.Clamp(limit, 1, MaxResults))
            .ToArray();

        return new
        {
            results = unique,
            jobs = jobMatches.Select(job => new { job.JobId, job.CorrelationId, job.State, job.ErrorCode }),
            executions = executionMatches,
            logs = logMatches
        };
    }

    private void AddSettings(List<SearchResult> results)
    {
        var document = JsonSerializer.SerializeToNode(settings.Read())?["settings"]?.AsObject();
        if (document is null) return;
        foreach (var item in Flatten(document))
            results.Add(new SearchResult("setting", item.Path.Split('.').Last(), $"{item.Path} · {item.Value}", item.Path.Split('.')[0], item.Path));
    }

    private void AddServiceProfiles(List<SearchResult> results) =>
        results.AddRange(serviceProfiles.List().Select(name => new SearchResult("profile", name, "Saved service configuration profile", "Performance", "service-profile-" + name)));

    private void AddWatermarkProfiles(List<SearchResult> results) =>
        results.AddRange(watermarkProfiles.List().Select(pair => new SearchResult("profile", pair.Key, $"Watermark profile · {pair.Value.Kind}", "watermarks", "watermark-profile-" + pair.Key)));

    private void AddWatchFolder(List<SearchResult> results)
    {
        results.Add(new SearchResult("watch-folder", "Watch folders", "Input, processing, output, originals, errors, and logs", "WatchFolder", "watch-folder-settings"));
        results.Add(new SearchResult("watch-folder", "Watch folder input", SafeFileName(watch.InputPath), "WatchFolder", "WatchFolder.InputPath"));
        results.Add(new SearchResult("watch-folder", "Watch folder processing", SafeFileName(watch.ProcessingPath), "WatchFolder", "WatchFolder.ProcessingPath"));
    }

    private static void AddDocumentation(List<SearchResult> results) =>
        results.AddRange(Documentation.Select(item => new SearchResult("documentation", Path.GetFileNameWithoutExtension(item.File), item.Summary, "overview", "documentation-" + item.File)));

    private static bool Matches(SearchResult result, string[] terms) =>
        terms.All(term => $"{result.Kind} {result.Title} {result.Summary} {result.Route} {result.Target}".Contains(term, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<(string Path, string Value)> Flatten(JsonObject node, string prefix = "")
    {
        foreach (var pair in node)
        {
            var path = string.IsNullOrEmpty(prefix) ? pair.Key : prefix + "." + pair.Key;
            if (pair.Value is JsonObject child)
            {
                foreach (var item in Flatten(child, path)) yield return item;
            }
            else if (pair.Value is not null)
            {
                yield return (path, pair.Value.ToJsonString());
            }
        }
    }

    private static string SafeFileName(string? value) => string.IsNullOrWhiteSpace(value) ? "Unavailable" : Path.GetFileName(value);
    private static string FormatDuration(double? value) => value is null ? "duration unavailable" : $"{value:0} ms";
}

public sealed record SearchResult(string Kind, string Title, string Summary, string Route, string Target);
