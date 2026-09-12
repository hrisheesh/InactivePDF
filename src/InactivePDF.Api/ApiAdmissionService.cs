using System.Globalization;
using System.Text.Json;
using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

public sealed record ApiAdmissionRequest(
    long RequestBytes,
    long InputBytes,
    long LargestFileBytes,
    int FileCount,
    bool Queued,
    int ExistingQueuedJobs = 0,
    string? Format = null);

public sealed record ApiAdmissionFailure(
    string Code,
    string Message,
    int StatusCode,
    int? RetryAfterSeconds,
    string Limit,
    string ConfiguredLimit);

/// <summary>Atomic per-key admission and successful-input quota accounting.</summary>
public sealed class ApiAdmissionService
{
    private readonly object gate = new();
    private readonly ApiKeyStore keys;
    private readonly ApiRequestLimits globalRequestLimits;
    private readonly ResourcePolicy globalResourcePolicy;
    private readonly Dictionary<Guid, RuntimeState> runtime = new();
    private readonly ApiUsageStore? apiUsage;
    private readonly Dictionary<Guid, UsageRecord> usage;
    private readonly string usagePath;
    private static readonly JsonSerializerOptions UsageJsonOptions = new() { WriteIndented = true };

    public ApiAdmissionService(ApiKeyStore keys, ApiRequestLimits globalRequestLimits, ResourcePolicy globalResourcePolicy, string usagePath, ApiUsageStore? apiUsage = null)
    {
        this.keys = keys;
        this.globalRequestLimits = globalRequestLimits;
        this.globalResourcePolicy = globalResourcePolicy;
        this.usagePath = Path.GetFullPath(usagePath);
        this.apiUsage = apiUsage;
        Directory.CreateDirectory(Path.GetDirectoryName(this.usagePath)!);
        WorkspacePathSecurity.EnsureSafeChain(Path.GetDirectoryName(this.usagePath)!, Path.GetDirectoryName(this.usagePath)!);
        usage = LoadUsage();
    }

    public ApiAdmissionFailure? TryAdmit(ApiIdentity? identity, ApiAdmissionRequest request, out ApiAdmissionLease? lease, DateTimeOffset? now = null)
    {
        lease = null;
        if (identity?.Kind is not ApiIdentityKind.Integration || identity.ApiKeyId is not { } keyId)
            return null;

        var metadata = keys.Get(keyId);
        if (metadata is null)
            return new ApiAdmissionFailure("api_key_unavailable", "The API key is no longer available. Retry with a valid key.", StatusCodes.Status401Unauthorized, null, "identity", "active key");

        var limits = metadata.Limits;
        var effectiveRequestBytes = Math.Min(limits.MaximumRequestBytes, globalRequestLimits.MaximumRequestBytes);
        var effectiveFileBytes = Math.Min(limits.MaximumFileBytes, Math.Min(globalRequestLimits.MaximumFileBytes, globalResourcePolicy.MaximumInputBytes));
        var effectiveFiles = Math.Min(limits.MaximumFilesPerRequest, globalRequestLimits.MaximumFiles);
        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (gate)
        {
            var state = GetState(keyId);
            while (state.Requests.Count > 0 && timestamp - state.Requests.Peek() >= TimeSpan.FromMinutes(1)) state.Requests.Dequeue();
            var currentUsage = CurrentUsage(keyId, timestamp);

            if (request.RequestBytes > effectiveRequestBytes)
                return Failure("request_bytes_exceeded", $"The request is {request.RequestBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes; this key allows {effectiveRequestBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes. Retrying will not help unless the request is smaller.", StatusCodes.Status413PayloadTooLarge, null, "maximum request bytes", effectiveRequestBytes);
            if (request.FileCount > effectiveFiles)
                return Failure("file_count_exceeded", $"The request contains {request.FileCount} files; this key allows {effectiveFiles.ToString("N0", CultureInfo.InvariantCulture)}. Retrying will not help unless fewer files are submitted.", StatusCodes.Status413PayloadTooLarge, null, "maximum files per request", effectiveFiles);
            if (request.LargestFileBytes > effectiveFileBytes)
                return Failure("file_bytes_exceeded", $"The largest file is {request.LargestFileBytes.ToString("N0", CultureInfo.InvariantCulture)} bytes; this key allows {effectiveFileBytes.ToString("N0", CultureInfo.InvariantCulture)}. Retrying will not help unless the file is smaller.", StatusCodes.Status413PayloadTooLarge, null, "maximum file bytes", effectiveFileBytes);
            if (limits.DailyInputBytes > 0 && currentUsage + state.ReservedInputBytes + request.InputBytes > limits.DailyInputBytes)
                return Failure("daily_input_quota_exceeded", $"The key has used {currentUsage.ToString("N0", CultureInfo.InvariantCulture)} successful input bytes today and allows {limits.DailyInputBytes.ToString("N0", CultureInfo.InvariantCulture)}. Retrying may succeed after the daily window resets or the quota is increased.", StatusCodes.Status429TooManyRequests, SecondsUntilNextDay(timestamp), "daily input bytes", limits.DailyInputBytes);
            if (state.Requests.Count >= limits.RequestsPerMinute)
                return Failure("requests_per_minute_exceeded", $"This key has reached its limit of {limits.RequestsPerMinute.ToString("N0", CultureInfo.InvariantCulture)} requests per minute. Retrying may succeed after the window moves.", StatusCodes.Status429TooManyRequests, RetryAfter(state.Requests.Peek(), timestamp), "requests per minute", limits.RequestsPerMinute);
            if (!request.Queued && state.ActiveConversions >= limits.ConcurrentConversions)
                return Failure("concurrent_conversions_exceeded", $"This key already has {state.ActiveConversions.ToString("N0", CultureInfo.InvariantCulture)} active conversions; its configured limit is {limits.ConcurrentConversions.ToString("N0", CultureInfo.InvariantCulture)}. Retrying may succeed when a conversion completes.", StatusCodes.Status429TooManyRequests, 1, "concurrent conversions", limits.ConcurrentConversions);
            if (request.Queued && Math.Max(state.QueuedJobs, request.ExistingQueuedJobs) >= limits.MaximumQueuedJobs)
                return Failure("queued_jobs_exceeded", $"This key has reached its configured queue limit of {limits.MaximumQueuedJobs.ToString("N0", CultureInfo.InvariantCulture)} jobs. Retrying may succeed when a queued job starts or completes.", StatusCodes.Status429TooManyRequests, 1, "maximum queued jobs", limits.MaximumQueuedJobs);

            state.Requests.Enqueue(timestamp);
            state.ReservedInputBytes += request.InputBytes;
            if (request.Queued) state.QueuedJobs++;
            else state.ActiveConversions++;
            apiUsage?.RecordAccepted(keyId, request.Format, timestamp);
            lease = new ApiAdmissionLease(this, keyId, request.InputBytes, request.Queued, request.Format, timestamp);
            return null;
        }

        ApiAdmissionFailure Failure(string code, string message, int statusCode, int? retryAfterSeconds, string limit, object configuredLimit)
        {
            if (statusCode == StatusCodes.Status429TooManyRequests) apiUsage?.RecordRateLimitRejection(keyId, code, timestamp);
            return new(code, message, statusCode, retryAfterSeconds, limit, Convert.ToString(configuredLimit, CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }

    public ApiKeyLimits? GetLimits(Guid? keyId) => keyId is { } id ? keys.Get(id)?.Limits : null;

    public long? GetMaximumOutputBytes(Guid? keyId)
    {
        var limits = GetLimits(keyId);
        return limits is null ? null : Math.Min(limits.MaximumOutputBytes, globalResourcePolicy.MaximumOutputBytes);
    }

    public bool IsJobExpired(Guid? keyId, DateTimeOffset acceptedAt, DateTimeOffset? now = null)
    {
        if (keyId is not { } owner) return false;
        var retentionDays = keys.Get(owner)?.Limits.JobRetentionDays ?? 0;
        return retentionDays > 0 && acceptedAt.AddDays(retentionDays) <= (now ?? DateTimeOffset.UtcNow);
    }

    public async ValueTask<ApiProcessingLease> BeginQueuedProcessingAsync(Guid jobId, Guid? keyId, CancellationToken cancellationToken = default)
    {
        if (keyId is not { } owner) return ApiProcessingLease.Empty;
        while (true)
        {
            lock (gate)
            {
                var state = GetState(owner);
                var limits = keys.Get(owner)?.Limits ?? ApiKeyLimits.Default;
                if (state.ActiveConversions < limits.ConcurrentConversions)
                {
                    state.QueuedJobs = Math.Max(0, state.QueuedJobs - 1);
                    state.ActiveConversions++;
                    var reservation = state.Reservations.TryGetValue(jobId, out var stored) ? stored : new Reservation(0, null, DateTimeOffset.UtcNow);
                    return new ApiProcessingLease(this, owner, jobId, reservation.Format, reservation.AcceptedAt, DateTimeOffset.UtcNow);
                }
            }
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    public void CompleteQueuedJob(Guid jobId, Guid? keyId, bool succeeded, long actualInputBytes, long outputBytes = 0,
        double? conversionMilliseconds = null, long cpuMilliseconds = 0, long peakMemoryBytes = 0, double? queueWaitMilliseconds = null)
    {
        if (keyId is not { } owner) return;
        lock (gate)
        {
            var state = GetState(owner);
            state.ActiveConversions = Math.Max(0, state.ActiveConversions - 1);
            var reservation = state.Reservations.Remove(jobId, out var stored) ? stored : new Reservation(0, null, DateTimeOffset.UtcNow);
            state.ReservedInputBytes = Math.Max(0, state.ReservedInputBytes - reservation.InputBytes);
            var completedAt = DateTimeOffset.UtcNow;
            if (succeeded) RecordSuccessUnsafe(owner, actualInputBytes > 0 ? actualInputBytes : reservation.InputBytes, completedAt);
            apiUsage?.RecordCompleted(owner, reservation.Format, succeeded, actualInputBytes > 0 ? actualInputBytes : reservation.InputBytes, outputBytes,
                conversionMilliseconds, queueWaitMilliseconds ?? Math.Max(0, (completedAt - reservation.AcceptedAt).TotalMilliseconds), cpuMilliseconds, peakMemoryBytes, completedAt);
        }
    }

    private void RequeueQueuedJob(Guid jobId, Guid keyId)
    {
        lock (gate)
        {
            var state = GetState(keyId);
            state.ActiveConversions = Math.Max(0, state.ActiveConversions - 1);
            state.QueuedJobs++;
            apiUsage?.RecordRetry(keyId);
        }
    }

    private void CommitSynchronous(Guid keyId, long reservedInputBytes, long actualInputBytes, string? format, DateTimeOffset acceptedAt,
        long outputBytes, double? conversionMilliseconds, long cpuMilliseconds, long peakMemoryBytes)
    {
        lock (gate)
        {
            var state = GetState(keyId);
            state.ActiveConversions = Math.Max(0, state.ActiveConversions - 1);
            state.ReservedInputBytes = Math.Max(0, state.ReservedInputBytes - reservedInputBytes);
            var completedAt = DateTimeOffset.UtcNow;
            RecordSuccessUnsafe(keyId, actualInputBytes > 0 ? actualInputBytes : reservedInputBytes, completedAt);
            apiUsage?.RecordCompleted(keyId, format, true, actualInputBytes > 0 ? actualInputBytes : reservedInputBytes, outputBytes,
                conversionMilliseconds ?? (completedAt - acceptedAt).TotalMilliseconds, 0, cpuMilliseconds, peakMemoryBytes, completedAt);
        }
    }

    private void Rollback(Guid keyId, long reservedInputBytes, bool queued, Guid? jobId, string? format, DateTimeOffset acceptedAt)
    {
        lock (gate)
        {
            var state = GetState(keyId);
            if (queued && jobId is { } id) state.Reservations.Remove(id);
            if (queued) state.QueuedJobs = Math.Max(0, state.QueuedJobs - 1);
            else state.ActiveConversions = Math.Max(0, state.ActiveConversions - 1);
            state.ReservedInputBytes = Math.Max(0, state.ReservedInputBytes - reservedInputBytes);
            if (jobId is null)
                apiUsage?.RecordCompleted(keyId, format, false, reservedInputBytes, 0, null, Math.Max(0, (DateTimeOffset.UtcNow - acceptedAt).TotalMilliseconds));
        }
    }

    private void CommitQueued(Guid keyId, Guid jobId, long reservedInputBytes, string? format, DateTimeOffset acceptedAt)
    {
        lock (gate)
        {
            var state = GetState(keyId);
            state.Reservations[jobId] = new Reservation(reservedInputBytes, format, acceptedAt);
        }
    }

    private RuntimeState GetState(Guid keyId) => runtime.TryGetValue(keyId, out var value) ? value : runtime[keyId] = new RuntimeState();

    private long CurrentUsage(Guid keyId, DateTimeOffset now) => usage.TryGetValue(keyId, out var value) && value.Day == DateOnly.FromDateTime(now.UtcDateTime) ? value.SuccessfulInputBytes : 0;

    private void RecordSuccessUnsafe(Guid keyId, long inputBytes, DateTimeOffset now)
    {
        if (inputBytes <= 0) return;
        var day = DateOnly.FromDateTime(now.UtcDateTime);
        var current = usage.TryGetValue(keyId, out var value) && value.Day == day ? value.SuccessfulInputBytes : 0;
        usage[keyId] = new UsageRecord(day, checked(current + inputBytes));
        PersistUsageUnsafe();
    }

    private Dictionary<Guid, UsageRecord> LoadUsage()
    {
        if (!File.Exists(usagePath)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<Guid, UsageRecord>>(File.ReadAllText(usagePath)) ?? new(); }
        catch (JsonException exception) { throw new InvalidDataException($"The API quota store '{usagePath}' is not valid JSON.", exception); }
    }

    private void PersistUsageUnsafe()
    {
        var temporary = usagePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024, FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, usage, UsageJsonOptions);
                stream.Flush(true);
            }
            WorkspacePathSecurity.EnsureSafeChain(Path.GetDirectoryName(usagePath)!, Path.GetDirectoryName(usagePath)!);
            if (File.Exists(usagePath))
            {
                try { File.Replace(temporary, usagePath, null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temporary, usagePath, overwrite: true); }
            }
            else File.Move(temporary, usagePath);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } }
    }

    private static int RetryAfter(DateTimeOffset oldest, DateTimeOffset now) => Math.Max(1, (int)Math.Ceiling((oldest + TimeSpan.FromMinutes(1) - now).TotalSeconds));
    private static int SecondsUntilNextDay(DateTimeOffset now) => Math.Max(1, (int)Math.Min(int.MaxValue, (now.UtcDateTime.Date.AddDays(1) - now.UtcDateTime).TotalSeconds));

    private sealed class RuntimeState
    {
        public Queue<DateTimeOffset> Requests { get; } = new();
        public int ActiveConversions { get; set; }
        public int QueuedJobs { get; set; }
        public long ReservedInputBytes { get; set; }
        public Dictionary<Guid, Reservation> Reservations { get; } = new();
    }

    private sealed record UsageRecord(DateOnly Day, long SuccessfulInputBytes);
    private sealed record Reservation(long InputBytes, string? Format, DateTimeOffset AcceptedAt);

    public sealed class ApiAdmissionLease : IDisposable
    {
        private readonly ApiAdmissionService owner;
        private readonly Guid keyId;
        private readonly long reservedInputBytes;
        private readonly bool queued;
        private readonly string? format;
        private readonly DateTimeOffset acceptedAt;
        private Guid? jobId;
        private int completed;

        internal ApiAdmissionLease(ApiAdmissionService owner, Guid keyId, long reservedInputBytes, bool queued, string? format, DateTimeOffset acceptedAt)
        { this.owner = owner; this.keyId = keyId; this.reservedInputBytes = reservedInputBytes; this.queued = queued; this.format = format; this.acceptedAt = acceptedAt; }

        public void CommitQueued(Guid jobId)
        {
            if (!queued) throw new InvalidOperationException("A synchronous admission cannot be committed as a queued job.");
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) throw new InvalidOperationException("The admission has already completed.");
            this.jobId = jobId;
            owner.CommitQueued(keyId, jobId, reservedInputBytes, format, acceptedAt);
        }

        public void CommitSuccess(long actualInputBytes, long outputBytes = 0, double? conversionMilliseconds = null, long cpuMilliseconds = 0, long peakMemoryBytes = 0)
        {
            if (queued) throw new InvalidOperationException("A queued admission must be committed with a job ID.");
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) return;
            owner.CommitSynchronous(keyId, reservedInputBytes, actualInputBytes, format, acceptedAt, outputBytes, conversionMilliseconds, cpuMilliseconds, peakMemoryBytes);
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref completed, 1, 0) != 0) return;
            owner.Rollback(keyId, reservedInputBytes, queued, jobId, format, acceptedAt);
        }
    }

    public sealed class ApiProcessingLease : IDisposable
    {
        private readonly ApiAdmissionService? owner;
        private readonly Guid keyId;
        private readonly Guid jobId;
        private readonly string? format;
        private readonly DateTimeOffset acceptedAt;
        private readonly DateTimeOffset processingStartedAt;
        private int released;
        internal static ApiProcessingLease Empty { get; } = new(null, Guid.Empty, Guid.Empty, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        internal ApiProcessingLease(ApiAdmissionService? owner, Guid keyId, Guid jobId, string? format, DateTimeOffset acceptedAt, DateTimeOffset processingStartedAt)
        { this.owner = owner; this.keyId = keyId; this.jobId = jobId; this.format = format; this.acceptedAt = acceptedAt; this.processingStartedAt = processingStartedAt; }
        public void Complete(bool succeeded, long actualInputBytes, long outputBytes = 0, double? conversionMilliseconds = null, long cpuMilliseconds = 0, long peakMemoryBytes = 0)
        {
            if (owner is not null && Interlocked.Exchange(ref released, 1) == 0)
                owner.CompleteQueuedJob(jobId, keyId, succeeded, actualInputBytes, outputBytes, conversionMilliseconds, cpuMilliseconds, peakMemoryBytes,
                    Math.Max(0, (processingStartedAt - acceptedAt).TotalMilliseconds));
        }

        public void Requeue()
        {
            if (owner is not null && Interlocked.Exchange(ref released, 1) == 0)
                owner.RequeueQueuedJob(jobId, keyId);
        }

        public void Dispose() => Complete(succeeded: false, actualInputBytes: 0);
    }
}

internal static class ApiAdmissionFailureExtensions
{
    public static IResult ToResult(this ApiAdmissionFailure failure, HttpContext context)
    {
        var requestId = context.TraceIdentifier;
        context.Response.Headers["X-Request-ID"] = requestId;
        if (failure.RetryAfterSeconds is { } seconds) context.Response.Headers["Retry-After"] = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(new
        {
            code = failure.Code,
            message = failure.Message,
            limit = failure.Limit,
            configuredLimit = failure.ConfiguredLimit,
            retryable = failure.StatusCode == StatusCodes.Status429TooManyRequests,
            requestId
        }, statusCode: failure.StatusCode);
    }
}
