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
        EnsureSafeStorePath();
        var loaded = File.Exists(_path)
            ? JsonSerializer.Deserialize<Dictionary<string, WatermarkOptions>>(File.ReadAllText(_path))
            : null;
        _profiles = loaded is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, WatermarkOptions>(loaded, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyDictionary<string, WatermarkOptions> List() { lock (_gate) return new Dictionary<string, WatermarkOptions>(_profiles, StringComparer.OrdinalIgnoreCase); }
    public static bool IsValidName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not "." and not "..";
    public bool TryGet(string name, out WatermarkOptions options) { lock (_gate) return _profiles.TryGetValue(name, out options!); }
    public void Save(string name, WatermarkOptions options) { ValidateName(name); lock (_gate) { _profiles[name] = options; Persist(); } }
    public bool Delete(string name) { ValidateName(name); lock (_gate) { var removed = _profiles.Remove(name); if (removed) Persist(); return removed; } }
    private static void ValidateName(string name)
    {
        if (!IsValidName(name))
            throw new ArgumentException("Profile name must be a single safe filename component.", nameof(name));
    }
    private void Persist()
    {
        EnsureSafeStorePath();
        var temporary = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(JsonSerializer.Serialize(_profiles, JsonOptions));
                writer.Flush();
                stream.Flush(true);
            }
            EnsureSafeStorePath();
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

    private void EnsureSafeStorePath()
    {
        var directory = Path.GetDirectoryName(_path)!;
        WorkspacePathSecurity.EnsureSafeChain(directory, directory);
        if (File.Exists(_path) && File.GetAttributes(_path).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("The watermark profile store cannot be a symbolic link or reparse point.");
    }
}
