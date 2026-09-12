using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Infrastructure.Watch;

public sealed class WatchFolderRetentionService(WatchFolderOptions folders, WatchFolderRetentionOptions options)
{
    public WatchFolderRetentionResult Sweep(DateTimeOffset now)
    {
        if (!options.Enabled) return WatchFolderRetentionResult.Disabled;

        var deletedFiles = 0;
        long deletedBytes = 0;
        foreach (var target in Targets())
        {
            if (!target.DeleteFiles || target.MaximumBytes <= 0 && options.MaximumAgeDays <= 0) continue;
            var result = SweepTarget(target, now);
            deletedFiles += result.DeletedFiles;
            deletedBytes = checked(deletedBytes + result.DeletedBytes);
        }

        return new WatchFolderRetentionResult(true, deletedFiles, deletedBytes);
    }

    private Target[] Targets() =>
    [
        new("output", folders.OutputPath, options.MaximumOutputBytes, options.DeleteOutputFiles, ProtectCurrentLog: false),
        new("originals", folders.OriginalsPath, options.MaximumOriginalsBytes, options.DeleteOriginalFiles, ProtectCurrentLog: false),
        new("errors", folders.ErrorsPath, options.MaximumErrorsBytes, options.DeleteErrorFiles, ProtectCurrentLog: false),
        new("logs", folders.LogPath, options.MaximumLogsBytes, options.DeleteLogFiles, ProtectCurrentLog: true)
    ];

    private TargetSweepResult SweepTarget(Target target, DateTimeOffset now)
    {
        if (!Directory.Exists(target.Path)) return default;
        try { WorkspacePathSecurity.EnsureSafeChain(target.Path, folders.RootPath); }
        catch (IOException) { return default; }
        catch (UnauthorizedAccessException) { return default; }

        var minimumFileTime = now.UtcDateTime - TimeSpan.FromSeconds(options.MinimumFileAgeSeconds);
        var protectedName = target.ProtectCurrentLog
            ? $"InactivePDF-{now:yyyyMMdd}"
            : null;
        var files = EnumerateFiles(target.Path, protectedName)
            .Where(file => file.LastWriteTimeUtc <= minimumFileTime)
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();

        var deleted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long deletedBytes = 0;
        var cutoff = options.MaximumAgeDays > 0
            ? now.UtcDateTime - TimeSpan.FromDays(options.MaximumAgeDays)
            : DateTime.MinValue;

        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc <= cutoff && TryDelete(file, deleted, ref deletedBytes, out _))
                continue;
        }

        if (target.MaximumBytes <= 0) return new(deleted.Count, deletedBytes);

        var remaining = EnumerateFiles(target.Path, protectedName)
            .Where(file => !deleted.Contains(file.FullName))
            .Sum(file => file.Length);
        foreach (var file in files)
        {
            if (remaining <= target.MaximumBytes) break;
            if (deleted.Contains(file.FullName)) continue;
            if (!TryDelete(file, deleted, ref deletedBytes, out var fileBytes)) continue;
            remaining = Math.Max(0, remaining - fileBytes);
        }

        return new(deleted.Count, deletedBytes);
    }

    private static IEnumerable<FileInfo> EnumerateFiles(string path, string? protectedName)
    {
        IEnumerable<string> paths;
        try { paths = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly); }
        catch (IOException) { yield break; }
        catch (UnauthorizedAccessException) { yield break; }

        foreach (var pathItem in paths)
        {
            var name = Path.GetFileName(pathItem);
            if (name.Length == 0 || name[0] == '.') continue;
            if (protectedName is not null && Path.GetFileNameWithoutExtension(name).StartsWith(protectedName, StringComparison.OrdinalIgnoreCase)) continue;

            FileInfo file;
            try
            {
                file = new FileInfo(pathItem);
                if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                WorkspacePathSecurity.EnsureSafeChain(file.FullName, path);
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            yield return file;
        }
    }

    private static bool TryDelete(FileInfo file, HashSet<string> deleted, ref long deletedBytes, out long fileBytes)
    {
        fileBytes = 0;
        try
        {
            var bytes = file.Length;
            file.Delete();
            deleted.Add(file.FullName);
            deletedBytes = checked(deletedBytes + bytes);
            fileBytes = bytes;
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private sealed record Target(string Name, string Path, long MaximumBytes, bool DeleteFiles, bool ProtectCurrentLog);
    private readonly record struct TargetSweepResult(int DeletedFiles, long DeletedBytes);
}

public sealed record WatchFolderRetentionResult(bool Enabled, int DeletedFiles, long DeletedBytes)
{
    public static WatchFolderRetentionResult Disabled { get; } = new(false, 0, 0);
}
