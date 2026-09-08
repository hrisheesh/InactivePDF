using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

/// <summary>Named complete configuration snapshots, separate from watermark/PDF output profiles.</summary>
public sealed class ServiceProfileStore(WorkspaceOptions workspace)
{
    private readonly object gate = new();
    private string Root => Path.Combine(workspace.RootPath, "service-profiles");
    private string Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
            throw new ArgumentException("Profile names use 1–64 letters, numbers, hyphens or underscores.");
        Directory.CreateDirectory(Root);
        return WorkspacePathSecurity.EnsureSafeChild(Root, Path.Combine(Root, name + ".json"));
    }
    public string[] List()
    {
        lock (gate)
        {
            if (!Directory.Exists(Root)) return [];
            WorkspacePathSecurity.EnsureSafeChain(Root, Root);
            return Directory.EnumerateFiles(Root, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order(StringComparer.Ordinal).ToArray();
        }
    }
    public JsonObject? Read(string name)
    {
        lock (gate) { var path = Resolve(name); return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null; }
    }
    public void Save(string name, JsonObject settings)
    {
        foreach (var section in JsonSerializer.SerializeToNode(new InactivePdfSettings())!.AsObject())
            if (settings[section.Key] is not JsonObject) throw new InvalidDataException(section.Key + " must be a complete settings section.");
        var typed = settings.Deserialize<InactivePdfSettings>() ?? throw new InvalidDataException("Settings required.");
        InactivePdfSettings.Validate(typed);
        lock (gate)
        {
            var path = Resolve(name);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { JsonSerializer.Serialize(stream, typed); stream.Flush(true); }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public bool Delete(string name)
    {
        lock (gate) { var path = Resolve(name); if (!File.Exists(path)) return false; File.Delete(path); return true; }
    }

    public static object Presets()
    {
        var cpu = Math.Max(1, Environment.ProcessorCount);
        var available = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        var budget = Math.Max(1207959552L, Math.Min(8L * 1024 * 1024 * 1024, available > 0 ? available / 2 : 2L * 1024 * 1024 * 1024));
        object Preset(string name, int count) => new
        {
            Performance = new PerformanceSettings { Profile = name, MaximumParallelWorkers = count, MemoryBudgetBytes = budget },
            WorkerCount = count, WatchConcurrency = count, OfficeConcurrency = Math.Max(1, Math.Min(count, (int)(budget / 1207959552L))),
            ImageConcurrency = Math.Max(1, Math.Min(count, (int)(budget / 1073741824L))), TextConcurrency = count, PdfConcurrency = count
        };
        return new { Conservative = Preset("Conservative", 1), Balanced = Preset("Balanced", Math.Min(2, cpu)), HighThroughput = Preset("High Throughput", Math.Min(8, cpu)) };
    }
}
