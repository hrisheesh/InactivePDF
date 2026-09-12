using System.Text.Json;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

/// <summary>Durable, secret-free usage accounting for integration API keys.</summary>
public sealed class ApiUsageStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private readonly string path;
    private readonly Dictionary<Guid, UsageRecord> records;

    public ApiUsageStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("The usage path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        EnsureSafePath();
        records = Load();
    }

    public void RecordAccepted(Guid keyId, string? format, DateTimeOffset now)
    {
        lock (gate)
        {
            var record = Get(keyId);
            record.TotalRequests++;
            record.LastRequestAt = now;
            GetFormat(record, format).Attempts++;
            PersistUnsafe();
        }
    }

    public void RecordRateLimitRejection(Guid keyId, string reason, DateTimeOffset now)
    {
        lock (gate)
        {
            var record = Get(keyId);
            record.RateLimitRejections++;
            record.LastRequestAt = now;
            record.RejectionReasons[NormalizeReason(reason)] = record.RejectionReasons.GetValueOrDefault(NormalizeReason(reason)) + 1;
            PersistUnsafe();
        }
    }

    public void RecordRetry(Guid keyId)
    {
        lock (gate)
        {
            Get(keyId).Retries++;
            PersistUnsafe();
        }
    }

    public void RecordCompleted(Guid keyId, string? format, bool succeeded, long inputBytes, long outputBytes,
        double? conversionMilliseconds, double queueWaitMilliseconds, long cpuMilliseconds = 0, long peakMemoryBytes = 0,
        DateTimeOffset? completedAt = null)
    {
        lock (gate)
        {
            var record = Get(keyId);
            var formatRecord = GetFormat(record, format);
            if (succeeded)
            {
                record.SuccessfulConversions++;
                record.BytesReceived = checked(record.BytesReceived + Math.Max(0, inputBytes));
                record.BytesProduced = checked(record.BytesProduced + Math.Max(0, outputBytes));
                record.SuccessfulInputBytes = checked(record.SuccessfulInputBytes + Math.Max(0, inputBytes));
                AddTiming(record.Conversion, conversionMilliseconds);
                formatRecord.Successful++;
                formatRecord.InputBytes = checked(formatRecord.InputBytes + Math.Max(0, inputBytes));
                formatRecord.OutputBytes = checked(formatRecord.OutputBytes + Math.Max(0, outputBytes));
                AddTiming(formatRecord.Conversion, conversionMilliseconds);
                record.LastSuccessAt = completedAt ?? DateTimeOffset.UtcNow;
            }
            else
            {
                record.FailedConversions++;
                record.BytesReceived = checked(record.BytesReceived + Math.Max(0, inputBytes));
                record.LastFailureAt = completedAt ?? DateTimeOffset.UtcNow;
            }

            AddTiming(record.QueueWait, queueWaitMilliseconds);
            record.CpuMilliseconds = checked(record.CpuMilliseconds + Math.Max(0, cpuMilliseconds));
            record.PeakMemoryBytes = Math.Max(record.PeakMemoryBytes, Math.Max(0, peakMemoryBytes));
            PersistUnsafe();
        }
    }

    public ApiKeyUsageSnapshot Snapshot(Guid keyId)
    {
        lock (gate) return ToSnapshot(keyId, records.GetValueOrDefault(keyId) ?? new UsageRecord());
    }

    public IReadOnlyList<ApiKeyUsageSnapshot> SnapshotAll(IEnumerable<ApiKeyMetadata>? keys = null)
    {
        lock (gate)
        {
            var ids = (keys ?? []).Select(key => key.Id).Concat(records.Keys).Distinct().Order().ToArray();
            return ids.Select(id => ToSnapshot(id, records.GetValueOrDefault(id) ?? new UsageRecord())).ToArray();
        }
    }

    private UsageRecord Get(Guid keyId) => records.TryGetValue(keyId, out var value) ? value : records[keyId] = new UsageRecord();

    private static FormatRecord GetFormat(UsageRecord record, string? format)
    {
        var name = NormalizeFormat(format);
        if (!record.Formats.TryGetValue(name, out var value)) record.Formats[name] = value = new FormatRecord();
        return value;
    }

    private Dictionary<Guid, UsageRecord> Load()
    {
        if (!File.Exists(path)) return new();
        EnsureSafePath();
        try { return JsonSerializer.Deserialize<Dictionary<Guid, UsageRecord>>(File.ReadAllText(path), JsonOptions) ?? new(); }
        catch (JsonException exception) { throw new InvalidDataException($"The API usage store '{path}' is not valid JSON.", exception); }
    }

    private void PersistUnsafe()
    {
        EnsureSafePath();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, records, JsonOptions);
                stream.Flush(true);
            }

            EnsureSafePath();
            if (File.Exists(path))
            {
                try { File.Replace(temporary, path, null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temporary, path, overwrite: true); }
            }
            else File.Move(temporary, path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private void EnsureSafePath()
    {
        var directory = Path.GetDirectoryName(path)!;
        WorkspacePathSecurity.EnsureSafeChain(directory, directory);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("The API usage store cannot be a symbolic link or reparse point.");
    }

    private static ApiKeyUsageSnapshot ToSnapshot(Guid id, UsageRecord record) => new(
        id, record.TotalRequests, record.SuccessfulConversions, record.FailedConversions,
        record.BytesReceived, record.BytesProduced, record.SuccessfulInputBytes - record.BytesProduced,
        record.SuccessfulInputBytes == 0 ? null : (record.SuccessfulInputBytes - record.BytesProduced) * 100d / record.SuccessfulInputBytes,
        ToTiming(record.Conversion), ToTiming(record.QueueWait), record.CpuMilliseconds, record.PeakMemoryBytes,
        record.Retries, record.TotalRequests == 0 ? null : record.Retries * 100d / record.TotalRequests,
        record.RateLimitRejections, record.LastRequestAt, record.LastSuccessAt, record.LastFailureAt,
        record.RejectionReasons.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
        record.Formats.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => new ApiKeyFormatUsage(
            pair.Key, pair.Value.Attempts, pair.Value.Successful, pair.Value.InputBytes, pair.Value.OutputBytes,
            pair.Value.InputBytes - pair.Value.OutputBytes,
            pair.Value.InputBytes == 0 ? null : (pair.Value.InputBytes - pair.Value.OutputBytes) * 100d / pair.Value.InputBytes,
            ToTiming(pair.Value.Conversion))).ToArray());

    private static UsageTiming ToTiming(TimingRecord value) => new(value.Count, value.Count == 0 ? null : value.TotalMilliseconds / value.Count, value.MinimumMilliseconds, value.MaximumMilliseconds);

    private static void AddTiming(TimingRecord target, double? milliseconds)
    {
        if (milliseconds is not { } value || double.IsNaN(value) || double.IsInfinity(value) || value < 0) return;
        target.Count++;
        target.TotalMilliseconds += value;
        target.MinimumMilliseconds = target.MinimumMilliseconds is null ? value : Math.Min(target.MinimumMilliseconds.Value, value);
        target.MaximumMilliseconds = target.MaximumMilliseconds is null ? value : Math.Max(target.MaximumMilliseconds.Value, value);
    }

    private static string NormalizeFormat(string? value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim().TrimStart('.').ToLowerInvariant();
    private static string NormalizeReason(string value) => string.IsNullOrWhiteSpace(value) ? "admission_rejected" : value.Trim().ToLowerInvariant();

    private sealed class UsageRecord
    {
        public long TotalRequests { get; set; }
        public long SuccessfulConversions { get; set; }
        public long FailedConversions { get; set; }
        public long BytesReceived { get; set; }
        public long BytesProduced { get; set; }
        public long SuccessfulInputBytes { get; set; }
        public long CpuMilliseconds { get; set; }
        public long PeakMemoryBytes { get; set; }
        public long Retries { get; set; }
        public long RateLimitRejections { get; set; }
        public DateTimeOffset? LastRequestAt { get; set; }
        public DateTimeOffset? LastSuccessAt { get; set; }
        public DateTimeOffset? LastFailureAt { get; set; }
        public TimingRecord Conversion { get; set; } = new();
        public TimingRecord QueueWait { get; set; } = new();
        public Dictionary<string, FormatRecord> Formats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long> RejectionReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class FormatRecord
    {
        public long Attempts { get; set; }
        public long Successful { get; set; }
        public long InputBytes { get; set; }
        public long OutputBytes { get; set; }
        public TimingRecord Conversion { get; set; } = new();
    }

    private sealed class TimingRecord
    {
        public long Count { get; set; }
        public double TotalMilliseconds { get; set; }
        public double? MinimumMilliseconds { get; set; }
        public double? MaximumMilliseconds { get; set; }
    }
}

public sealed record UsageTiming(long Count, double? AverageMilliseconds, double? MinimumMilliseconds, double? MaximumMilliseconds);

public sealed record ApiKeyFormatUsage(
    string Format, long Attempts, long Successful, long InputBytes, long OutputBytes, long BytesSaved, double? SavingsPercent, UsageTiming Conversion);

public sealed record ApiKeyUsageSnapshot(
    Guid ApiKeyId, long TotalRequests, long SuccessfulConversions, long FailedConversions,
    long BytesReceived, long BytesProduced, long BytesSaved, double? SavingsPercent,
    UsageTiming Conversion, UsageTiming QueueWait, long CpuMilliseconds, long PeakMemoryBytes,
    long Retries, double? RetryRatePercent, long RateLimitRejections, DateTimeOffset? LastRequestAt,
    DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, IReadOnlyDictionary<string, long> RejectionReasons,
    IReadOnlyList<ApiKeyFormatUsage> Formats);
