using System.Text.Json;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

public sealed class WatermarkProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, WatermarkOptions> _profiles;

    public WatermarkProfileStore(WorkspaceOptions options)
    {
        _path = Path.Combine(options.RootPath, "watermark-profiles.json");
        Directory.CreateDirectory(options.RootPath);
        _profiles = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, WatermarkOptions>>(File.ReadAllText(_path)) ?? new(StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, WatermarkOptions> List() { lock (_gate) return new Dictionary<string, WatermarkOptions>(_profiles, StringComparer.OrdinalIgnoreCase); }
    public bool TryGet(string name, out WatermarkOptions options) { lock (_gate) return _profiles.TryGetValue(name, out options!); }
    public void Save(string name, WatermarkOptions options) { ValidateName(name); lock (_gate) { _profiles[name] = options; Persist(); } }
    public bool Delete(string name) { ValidateName(name); lock (_gate) { var removed = _profiles.Remove(name); if (removed) Persist(); return removed; } }
    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            throw new ArgumentException("Profile name must be a single safe filename component.", nameof(name));
    }
    private void Persist()
    {
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(_profiles, JsonOptions));
            if (File.Exists(_path))
            {
                try { File.Replace(temporary, _path, null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temporary, _path, overwrite: true); }
            }
            else File.Move(temporary, _path);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { }
        }
    }
}
