using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using InactivePDF.Application;
using InactivePDF.Application.Abstractions;
using InactivePDF.Application.Models;
using InactivePDF.Application.Policies;
using InactivePDF.Domain.Models;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace InactivePDF.Infrastructure.Jobs;

/// <summary>
/// Durable single-node job store backed by LiteDB's ACID journaled data file.
/// Pending work is claimed with an expiring lease so a crashed worker can be recovered
/// without allowing two live workers to complete the same job concurrently.
/// </summary>
public sealed class LiteDbJobStore : IJobPersistence, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private readonly LiteDatabase _database;
    private readonly ILiteCollection<BsonDocument> _jobs;
    private readonly ILiteCollection<BsonDocument> _pending;
    private readonly ILiteCollection<BsonDocument> _deadLetters;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public LiteDbJobStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var fullPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _database = new LiteDatabase($"Filename={fullPath};Connection=shared");
        _jobs = _database.GetCollection<BsonDocument>("jobs");
        _pending = _database.GetCollection<BsonDocument>("pending_work");
        _deadLetters = _database.GetCollection<BsonDocument>("dead_letters");
        _jobs.EnsureIndex(document => document["correlationId"]);
        _jobs.EnsureIndex(document => document["acceptedAt"]);
        _jobs.EnsureIndex(document => document["updatedAt"]);
        _pending.EnsureIndex(document => document["createdAt"]);
        _pending.EnsureIndex(document => document["nextAttemptAt"]);
        _pending.EnsureIndex(document => document["leaseExpiresAt"]);
        RecoverInterruptedJobs();
    }

    public Task<JobStatus?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadStatus(_jobs.FindById(jobId.ToString("N"))));
    }

    public IReadOnlyList<JobStatus> ListRecent(int limit = 50)
    {
        return _jobs.Find(Query.All("updatedAt", Query.Descending), limit: Math.Clamp(limit, 1, 100))
            .Select(ReadStatus).OfType<JobStatus>().ToArray();
    }

    public int PendingCount => _pending.Count();

    private void RecoverInterruptedJobs()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var document in _jobs.FindAll().ToList())
        {
            var status = ReadStatus(document);
            if (status?.State is not (ConversionJobState.Processing or ConversionJobState.WaitingForResources or ConversionJobState.Running)) continue;
            _jobs.Update(ToDocument(status with
            {
                State = ConversionJobState.Interrupted,
                UpdatedAt = now,
                ErrorCode = "service_restarted_before_completion",
                ErrorMessage = "The service restarted before this job completed.",
                CurrentWorker = null,
                CurrentLane = null
            }, ReadNullableString(document, "requestFingerprint")));
        }
    }

    public int PendingCountForOwner(Guid ownerApiKeyId) => _pending.FindAll()
        .Count(document => IsNotDeadLetter(document) && ReadNullableGuid(document, "ownerApiKeyId") == ownerApiKeyId);

    public IReadOnlyList<JobStatus> SearchJobs(string query)
    {
        var text = query.Trim();
        var idText = text.Replace("-", "", StringComparison.Ordinal);
        var filter = Query.Or(Query.Contains("_id", idText.Length > 0 ? idText : text), Query.Contains("correlationId", text), Query.Contains("errorCode", text));
        if (Enum.TryParse<ConversionJobState>(text, true, out var state)) filter = Query.Or(filter, Query.EQ("state", (int)state));
        return _jobs.Find(filter, limit: 30).Select(ReadStatus).OfType<JobStatus>().ToArray();
    }

    public Task<JobPage> ListAsync(JobQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var limit = Math.Clamp(query.Limit, 1, 100);
        var jobs = _jobs.FindAll()
            .Select(ReadStatus)
            .OfType<JobStatus>()
            .Where(status => query.OwnerApiKeyId is null || status.OwnerApiKeyId == query.OwnerApiKeyId)
            .Where(status => query.State is null || status.State == query.State)
            .Where(status => string.IsNullOrWhiteSpace(query.Source) || string.Equals(status.Source, query.Source.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(status => string.IsNullOrWhiteSpace(query.Format) || string.Equals(status.Format, query.Format.Trim().TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            .Where(status => query.CreatedAfter is null || status.AcceptedAt >= query.CreatedAfter)
            .Where(status => query.CreatedBefore is null || status.AcceptedAt <= query.CreatedBefore)
            .OrderByDescending(status => status.AcceptedAt)
            .ThenByDescending(status => status.JobId)
            .ToList();

        if (!string.IsNullOrWhiteSpace(query.Cursor))
        {
            if (!TryDecodeCursor(query.Cursor, out var cursorDate, out var cursorId))
                throw new ArgumentException("The cursor is invalid.", nameof(query));
            jobs = jobs.Where(status => status.AcceptedAt < cursorDate || status.AcceptedAt == cursorDate && status.JobId.CompareTo(cursorId) < 0).ToList();
        }

        var page = jobs.Take(limit).ToArray();
        var nextCursor = jobs.Count > limit && page.Length > 0 ? EncodeCursor(page[^1]) : null;
        return Task.FromResult(new JobPage(page, nextCursor));
    }

    public Task<JobStatus?> FindByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        cancellationToken.ThrowIfCancellationRequested();
        var document = _jobs.FindOne(Query.EQ("correlationId", correlationId));
        return Task.FromResult(ReadStatus(document));
    }

    public async Task UpsertAsync(JobStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _jobs.Upsert(ToDocumentPreservingFingerprint(status));
        }
        finally { _writeGate.Release(); }
    }

    public Task<JobStatus?> CreateIfAbsentAsync(
        JobStatus status,
        ConversionWorkItem pendingWork,
        CancellationToken cancellationToken = default) =>
        CreateIfAbsentAsync(status, pendingWork, requestFingerprint: null, cancellationToken);

    public async Task<JobStatus?> CreateIfAbsentAsync(
        JobStatus status,
        ConversionWorkItem pendingWork,
        string? requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(pendingWork);
        if (requestFingerprint is not null) ArgumentException.ThrowIfNullOrWhiteSpace(requestFingerprint);
        return await CreateIfAbsentCoreAsync(status, pendingWork, requestFingerprint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobStatus?> CreateIfAbsentCoreAsync(
        JobStatus status,
        ConversionWorkItem pendingWork,
        string? requestFingerprint,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginTransactionOrThrow();
            var existing = _jobs.FindOne(Query.EQ("correlationId", status.CorrelationId));
            if (existing is not null)
            {
                var existingStatus = ReadStatus(existing);
                var expiresAt = existingStatus?.IdempotencyExpiresAt ?? existingStatus?.AcceptedAt.AddHours(24);
                if (expiresAt is { } expiry && expiry <= DateTimeOffset.UtcNow)
                {
                    _jobs.Delete(existing["_id"].AsString);
                    _pending.Delete(existing["_id"].AsString);
                    _deadLetters.Delete(existing["_id"].AsString);
                }
                else
                {
                    EnsureMatchingFingerprint(existing, status.CorrelationId, requestFingerprint);
                    _database.Rollback();
                    return existingStatus;
                }
            }

            _jobs.Insert(ToDocument(status, requestFingerprint));
            _pending.Insert(ToPendingDocument(pendingWork, DateTimeOffset.UtcNow));
            _database.Commit();
            return null;
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<JobStatus?> CancelAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = _jobs.FindById(jobId.ToString("N"));
            var current = ReadStatus(document);
            if (current is null) return null;
            if (current.State is ConversionJobState.Succeeded or ConversionJobState.Failed or ConversionJobState.Cancelled or ConversionJobState.DeadLettered)
                return current;

            BeginTransactionOrThrow();
            var updated = current with
            {
                State = ConversionJobState.Cancelled,
                UpdatedAt = now,
                ErrorCode = "cancelled",
                ErrorMessage = "The job was cancelled by the client.",
                CurrentWorker = null,
                CurrentLane = null
            };
            _jobs.Upsert(ToDocumentPreservingFingerprint(updated));
            if (current.State is not ConversionJobState.Processing and not ConversionJobState.Running)
                _pending.Delete(jobId.ToString("N"));
            _database.Commit();
            return updated;
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<JobStatus?> RetryAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadStatus(_jobs.FindById(jobId.ToString("N")));
            if (current is null) return null;
            if (current.State is not (ConversionJobState.Failed or ConversionJobState.DeadLettered or ConversionJobState.Interrupted))
                return current;
            var deadLetter = ReadDeadLetter(_deadLetters.FindById(jobId.ToString("N")));
            var work = deadLetter?.WorkItem;
            if (work is null)
            {
                var pending = _pending.FindById(jobId.ToString("N"));
                work = pending is null ? null : DeserializePending(pending);
            }
            if (work is null) throw new InvalidOperationException("The job has no retryable work payload.");
            if (work.Inputs.Any(input => !File.Exists(input.Path)))
                throw new FileNotFoundException("The original input is no longer available for retry.");

            work = work with
            {
                Job = work.Job with { State = ConversionJobState.Queued },
                Attempt = Math.Max(work.Attempt, current.Attempts),
                LeaseOwner = null
            };
            var updated = current with
            {
                State = ConversionJobState.Queued,
                UpdatedAt = now,
                ErrorCode = null,
                ErrorMessage = null,
                OutputPath = null,
                ProgressPercent = 0,
                CurrentWorker = null,
                CurrentLane = null,
                QueueWaitMilliseconds = 0,
                ProcessingMilliseconds = 0,
                OutputBytes = 0,
                CompressionPercent = null
            };

            BeginTransactionOrThrow();
            _jobs.Upsert(ToDocumentPreservingFingerprint(updated));
            _pending.Upsert(ToPendingDocument(work, now));
            _database.Commit();
            return updated;
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<JobStatus?> DeleteAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadStatus(_jobs.FindById(jobId.ToString("N")));
            if (current is null) return null;
            BeginTransactionOrThrow();
            _jobs.Delete(jobId.ToString("N"));
            _pending.Delete(jobId.ToString("N"));
            _deadLetters.Delete(jobId.ToString("N"));
            _database.Commit();
            return current;
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async IAsyncEnumerable<ConversionWorkItem> ReadPendingAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var payloads = _pending.FindAll()
            .Where(IsNotDeadLetter)
            .OrderBy(document => ReadDate(document, "createdAt"))
            .ToList();
        foreach (var payload in payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = DeserializePending(payload);
            if (item is not null) yield return item;
            await Task.Yield();
        }
    }

    public async Task<IReadOnlyList<ConversionWorkItem>> ClaimPendingAsync(
        string leaseOwner,
        int maximumItems,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumItems, 0);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = _pending.FindAll()
                .Where(document => IsClaimable(document, now))
                .OrderBy(document => ReadDate(document, "createdAt"))
                .Take(maximumItems * 4)
                .ToList();
            var claimed = new List<ConversionWorkItem>(Math.Min(maximumItems, candidates.Count));
            foreach (var candidate in candidates)
            {
                if (claimed.Count == maximumItems) break;
                cancellationToken.ThrowIfCancellationRequested();

                var id = candidate["_id"].AsString;
                BeginTransactionOrThrow();
                try
                {
                    var current = _pending.FindById(id);
                    if (current is null || !IsClaimable(current, now))
                    {
                        _database.Rollback();
                        continue;
                    }

                    var expiresAt = now + leaseDuration;
                    current["state"] = "leased";
                    current["leaseOwner"] = leaseOwner;
                    current["leaseExpiresAt"] = expiresAt.ToString("O");
                    current["updatedAt"] = now.ToString("O");
                    _pending.Update(current);
                    _database.Commit();

                    if (DeserializePending(current) is { } item)
                        claimed.Add(item with { LeaseOwner = leaseOwner });
                }
                catch
                {
                    try { _database.Rollback(); } catch (InvalidOperationException) { }
                    throw;
                }
            }

            return claimed;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<bool> RenewLeaseAsync(
        Guid jobId,
        string leaseOwner,
        TimeSpan leaseDuration,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseDuration, TimeSpan.Zero);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginTransactionOrThrow();
            var pending = _pending.FindById(jobId.ToString("N"));
            if (pending is null || !HasLease(pending, leaseOwner))
            {
                _database.Rollback();
                return false;
            }

            pending["leaseExpiresAt"] = (now + leaseDuration).ToString("O");
            pending["updatedAt"] = now.ToString("O");
            _pending.Update(pending);
            _database.Commit();
            return true;
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async Task ReleaseLeaseAsync(
        ConversionWorkItem work,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string? errorCode = null,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginTransactionOrThrow();
            var pending = _pending.FindById(work.Job.Id.ToString("N"));
            if (pending is null || !HasLease(pending, leaseOwner))
            {
                _database.Rollback();
                return;
            }

            SetPendingState(pending, "pending", nextAttemptAt, work.Attempt, errorCode, errorMessage);
            _pending.Update(pending);
            _database.Commit();
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async Task ScheduleRetryAsync(
        JobStatus status,
        ConversionWorkItem work,
        string leaseOwner,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        string errorMessage,
        Exception exception,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentNullException.ThrowIfNull(exception);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginTransactionOrThrow();
            var pending = RequireLease(work.Job.Id, leaseOwner);
            _jobs.Upsert(ToDocumentPreservingFingerprint(status with { State = ConversionJobState.Retrying, ErrorCode = errorCode, ErrorMessage = errorMessage }));
            SetPendingState(pending, "pending", nextAttemptAt, work.Attempt, errorCode, errorMessage);
            AppendAttempt(pending, CreateAttemptDiagnostic(work.Attempt, errorCode, exception, retryable: true));
            _pending.Update(pending);
            _database.Commit();
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<IReadOnlyList<DeadLetterRecord>> ListDeadLettersAsync(
        int maximumItems,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumItems, 0);
        maximumItems = Math.Min(maximumItems, 1_000);
        cancellationToken.ThrowIfCancellationRequested();
        return _deadLetters.FindAll()
            .OrderByDescending(document => ReadDate(document, "createdAt"))
            .Take(maximumItems)
            .Select(ReadDeadLetter)
            .Where(record => record is not null)
            .Cast<DeadLetterRecord>()
            .ToArray();
    }

    public Task<DeadLetterRecord?> GetDeadLetterAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadDeadLetter(_deadLetters.FindById(jobId.ToString("N"))));
    }

    public async Task RemovePendingAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pending.Delete(jobId.ToString("N"));
        }
        finally { _writeGate.Release(); }
    }

    public Task CompleteSuccessAsync(JobStatus status, CancellationToken cancellationToken = default) =>
        CompleteSuccessCoreAsync(status, leaseOwner: null, cancellationToken);

    public Task CompleteSuccessWithLeaseAsync(JobStatus status, string leaseOwner, CancellationToken cancellationToken = default) =>
        CompleteSuccessCoreAsync(status, leaseOwner, cancellationToken);

    private async Task CompleteSuccessCoreAsync(JobStatus status, string? leaseOwner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginTransactionOrThrow();
            var pending = _pending.FindById(status.JobId.ToString("N"));
            if (leaseOwner is not null && (pending is null || !HasLease(pending, leaseOwner)))
                throw new ConversionLeaseLostException(status.JobId);
            if (ReadStatus(_jobs.FindById(status.JobId.ToString("N")))?.State == ConversionJobState.Cancelled)
            {
                _pending.Delete(status.JobId.ToString("N"));
                _database.Commit();
                return;
            }
            _jobs.Upsert(ToDocumentPreservingFingerprint(status));
            _pending.Delete(status.JobId.ToString("N"));
            _database.Commit();
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public Task CompleteFailureAsync(
        JobStatus status,
        ConversionWorkItem work,
        Exception exception,
        CancellationToken cancellationToken = default) =>
        CompleteFailureCoreAsync(status, work, exception, leaseOwner: null, "conversion_failed", cancellationToken);

    public Task CompleteFailureWithLeaseAsync(
        JobStatus status,
        ConversionWorkItem work,
        Exception exception,
        string leaseOwner,
        string errorCode,
        CancellationToken cancellationToken = default) =>
        CompleteFailureCoreAsync(status, work, exception, leaseOwner, errorCode, cancellationToken);

    private async Task CompleteFailureCoreAsync(
        JobStatus status,
        ConversionWorkItem work,
        Exception exception,
        string? leaseOwner,
        string errorCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(exception);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeginTransactionOrThrow();
            var pending = _pending.FindById(status.JobId.ToString("N"));
            if (leaseOwner is not null && (pending is null || !HasLease(pending, leaseOwner)))
                throw new ConversionLeaseLostException(status.JobId);

            var classification = ConversionFailureClassifier.Classify(exception);
            _jobs.Upsert(ToDocumentPreservingFingerprint(status with
            {
                State = ConversionJobState.DeadLettered,
                ErrorCode = errorCode,
                ErrorMessage = classification.Message
            }));
            _pending.Delete(status.JobId.ToString("N"));
            var attemptHistory = ReadAttemptHistory(pending);
            AppendAttempt(attemptHistory, CreateAttemptDiagnostic(work.Attempt, errorCode, exception, retryable: false));
            _deadLetters.Upsert(new BsonDocument
            {
                ["_id"] = work.Job.Id.ToString("N"),
                ["correlationId"] = work.Job.CorrelationId,
                ["operation"] = (int)work.Job.Operation,
                ["attempt"] = work.Attempt,
                ["errorCode"] = errorCode,
                ["errorType"] = exception.GetType().Name,
                ["error"] = classification.Message,
                ["payload"] = JsonSerializer.Serialize(work, JsonOptions),
                ["attemptHistory"] = JsonSerializer.Serialize(attemptHistory, JsonOptions),
                ["createdAt"] = DateTimeOffset.UtcNow.ToString("O")
            });
            _database.Commit();
        }
        catch
        {
            try { _database.Rollback(); } catch (InvalidOperationException) { }
            throw;
        }
        finally { _writeGate.Release(); }
    }

    public void Dispose()
    {
        _database.Dispose();
        _writeGate.Dispose();
    }

    private BsonDocument RequireLease(Guid jobId, string leaseOwner)
    {
        var pending = _pending.FindById(jobId.ToString("N"));
        if (pending is null || !HasLease(pending, leaseOwner)) throw new ConversionLeaseLostException(jobId);
        return pending;
    }

    private static void SetPendingState(BsonDocument document, string state, DateTimeOffset nextAttemptAt, int attempt, string? errorCode, string? errorMessage)
    {
        document["state"] = state;
        document["attempt"] = attempt;
        document["nextAttemptAt"] = nextAttemptAt.ToString("O");
        document["leaseOwner"] = BsonValue.Null;
        document["leaseExpiresAt"] = BsonValue.Null;
        document["updatedAt"] = DateTimeOffset.UtcNow.ToString("O");
        document["lastErrorCode"] = errorCode is null ? BsonValue.Null : errorCode;
        document["lastErrorMessage"] = errorMessage is null ? BsonValue.Null : errorMessage;
    }

    private static BsonDocument ToPendingDocument(ConversionWorkItem work, DateTimeOffset now) => new()
    {
        ["_id"] = work.Job.Id.ToString("N"),
        ["payload"] = JsonSerializer.Serialize(work with { LeaseOwner = null }, JsonOptions),
        ["state"] = "pending",
        ["ownerApiKeyId"] = work.Job.OwnerApiKeyId?.ToString("N") ?? BsonValue.Null,
        ["attempt"] = work.Attempt,
        ["createdAt"] = now.ToString("O"),
        ["updatedAt"] = now.ToString("O"),
        ["nextAttemptAt"] = now.ToString("O"),
        ["leaseOwner"] = BsonValue.Null,
        ["leaseExpiresAt"] = BsonValue.Null,
        ["lastErrorCode"] = BsonValue.Null,
        ["lastErrorMessage"] = BsonValue.Null,
        ["attemptHistory"] = JsonSerializer.Serialize(Array.Empty<ConversionAttemptDiagnostic>(), JsonOptions)
    };

    private static BsonDocument ToDocument(JobStatus status, string? requestFingerprint = null) => new()
    {
        ["_id"] = status.JobId.ToString("N"),
        ["correlationId"] = status.CorrelationId,
        ["operation"] = (int)status.Operation,
        ["state"] = (int)status.State,
        ["acceptedAt"] = status.AcceptedAt.ToString("O"),
        ["updatedAt"] = status.UpdatedAt.ToString("O"),
        ["attempts"] = status.Attempts,
        ["ownerApiKeyId"] = status.OwnerApiKeyId?.ToString("N") ?? BsonValue.Null,
        ["outputPath"] = status.OutputPath is null ? BsonValue.Null : status.OutputPath,
        ["errorCode"] = status.ErrorCode is null ? BsonValue.Null : status.ErrorCode,
        ["errorMessage"] = status.ErrorMessage is null ? BsonValue.Null : status.ErrorMessage,
        ["requestFingerprint"] = requestFingerprint is null ? BsonValue.Null : requestFingerprint,
        ["source"] = status.Source,
        ["format"] = status.Format is null ? BsonValue.Null : status.Format,
        ["progressPercent"] = status.ProgressPercent,
        ["currentWorker"] = status.CurrentWorker is null ? BsonValue.Null : status.CurrentWorker,
        ["currentLane"] = status.CurrentLane is null ? BsonValue.Null : status.CurrentLane,
        ["queueWaitMilliseconds"] = status.QueueWaitMilliseconds,
        ["processingMilliseconds"] = status.ProcessingMilliseconds,
        ["inputBytes"] = status.InputBytes,
        ["outputBytes"] = status.OutputBytes,
        ["compressionPercent"] = status.CompressionPercent is null ? BsonValue.Null : status.CompressionPercent.Value,
        ["idempotencyExpiresAt"] = status.IdempotencyExpiresAt?.ToString("O") ?? BsonValue.Null,
        ["retryCount"] = status.RetryCount
    };

    private BsonDocument ToDocumentPreservingFingerprint(JobStatus status) =>
        ToDocument(status, ReadNullableString(_jobs.FindById(status.JobId.ToString("N")) ?? new BsonDocument(), "requestFingerprint"));

    private static ConversionWorkItem? DeserializePending(BsonDocument document)
    {
        var item = JsonSerializer.Deserialize<ConversionWorkItem>(document["payload"].AsString, JsonOptions);
        if (item is null) return null;
        var attempt = document.TryGetValue("attempt", out var value) && !value.IsNull ? value.AsInt32 : item.Attempt;
        var owner = ReadNullableString(document, "leaseOwner");
        return item with { Attempt = attempt, LeaseOwner = owner };
    }

    private static bool IsNotDeadLetter(BsonDocument document) =>
        !string.Equals(ReadNullableString(document, "state"), "dead", StringComparison.OrdinalIgnoreCase);

    private static bool IsClaimable(BsonDocument document, DateTimeOffset now)
    {
        if (!IsNotDeadLetter(document)) return false;
        var nextAttempt = ReadDate(document, "nextAttemptAt");
        if (nextAttempt > now) return false;
        var state = ReadNullableString(document, "state");
        if (!string.Equals(state, "leased", StringComparison.OrdinalIgnoreCase)) return true;
        return ReadDate(document, "leaseExpiresAt") <= now;
    }

    private static bool HasLease(BsonDocument document, string leaseOwner) =>
        string.Equals(ReadNullableString(document, "leaseOwner"), leaseOwner, StringComparison.Ordinal);

    private static void EnsureMatchingFingerprint(BsonDocument existing, string correlationId, string? requestFingerprint)
    {
        if (string.IsNullOrWhiteSpace(requestFingerprint)) return;
        var existingFingerprint = ReadNullableString(existing, "requestFingerprint");
        if (!string.Equals(existingFingerprint, requestFingerprint, StringComparison.Ordinal))
            throw new IdempotencyConflictException(correlationId);
    }

    private void BeginTransactionOrThrow()
    {
        if (!_database.BeginTrans()) throw new IOException("The durable job store could not begin a transaction.");
    }

    private static JobStatus? ReadStatus(BsonDocument? document)
    {
        if (document is null) return null;
        return new JobStatus(
            Guid.ParseExact(document["_id"].AsString, "N"),
            document["correlationId"].AsString,
            (ConversionOperation)document["operation"].AsInt32,
            (ConversionJobState)document["state"].AsInt32,
            DateTimeOffset.Parse(document["acceptedAt"].AsString, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(document["updatedAt"].AsString, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            document["attempts"].AsInt32,
            ReadNullableString(document, "outputPath"),
            ReadNullableString(document, "errorCode"),
            ReadNullableString(document, "errorMessage"),
            ReadNullableGuid(document, "ownerApiKeyId"),
            ReadNullableString(document, "source") ?? "API",
            ReadNullableString(document, "format"),
            ReadInt(document, "progressPercent"),
            ReadNullableString(document, "currentWorker"),
            ReadNullableString(document, "currentLane"),
            ReadLong(document, "queueWaitMilliseconds"),
            ReadLong(document, "processingMilliseconds"),
            ReadLong(document, "inputBytes"),
            ReadLong(document, "outputBytes"),
            ReadNullableDouble(document, "compressionPercent"),
            ReadNullableDate(document, "idempotencyExpiresAt"),
            ReadInt(document, "retryCount", Math.Max(0, ReadInt(document, "attempts") - 1)));
    }

    private static int ReadInt(BsonDocument document, string key, int fallback = 0) => document.TryGetValue(key, out var value) && !value.IsNull ? value.AsInt32 : fallback;
    private static long ReadLong(BsonDocument document, string key) => document.TryGetValue(key, out var value) && !value.IsNull ? value.AsInt64 : 0;
    private static double? ReadNullableDouble(BsonDocument document, string key) => document.TryGetValue(key, out var value) && !value.IsNull ? value.AsDouble : null;
    private static DateTimeOffset? ReadNullableDate(BsonDocument document, string key) =>
        DateTimeOffset.TryParse(ReadNullableString(document, key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : null;

    private static DateTimeOffset ReadDate(BsonDocument document, string key) =>
        document.TryGetValue(key, out var value) && !value.IsNull && DateTimeOffset.TryParse(value.AsString, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : DateTimeOffset.MinValue;

    private static string? ReadNullableString(BsonDocument document, string key) =>
        !document.TryGetValue(key, out var value) || value.IsNull ? null : value.AsString;

    private static Guid? ReadNullableGuid(BsonDocument document, string key) =>
        Guid.TryParseExact(ReadNullableString(document, key), "N", out var value) ? value : null;

    private static string EncodeCursor(JobStatus status)
    {
        var value = $"{status.AcceptedAt.UtcTicks}:{status.JobId:N}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
    }

    private static bool TryDecodeCursor(string value, out DateTimeOffset acceptedAt, out Guid jobId)
    {
        acceptedAt = default;
        jobId = default;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
            var parts = decoded.Split(':', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks) || !Guid.TryParseExact(parts[1], "N", out jobId))
                return false;
            acceptedAt = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch (FormatException) { return false; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    private static DeadLetterRecord? ReadDeadLetter(BsonDocument? document)
    {
        if (document is null) return null;
        var payload = JsonSerializer.Deserialize<ConversionWorkItem>(document["payload"].AsString, JsonOptions);
        if (payload is null) return null;
        return new DeadLetterRecord(
            Guid.ParseExact(document["_id"].AsString, "N"),
            document["correlationId"].AsString,
            (ConversionOperation)document["operation"].AsInt32,
            document["attempt"].AsInt32,
            document["errorCode"].AsString,
            document["errorType"].AsString,
            ConversionFailureClassifier.SafeMessageForCode(document["errorCode"].AsString),
            ReadDate(document, "createdAt"),
            ReadAttemptHistory(document),
            payload with { LeaseOwner = null });
    }

    private static List<ConversionAttemptDiagnostic> ReadAttemptHistory(BsonDocument document)
    {
        var serialized = ReadNullableString(document, "attemptHistory");
        if (string.IsNullOrWhiteSpace(serialized)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<ConversionAttemptDiagnostic>>(serialized, JsonOptions) ?? [])
                .Select(attempt => attempt with
                {
                    Message = ConversionFailureClassifier.SafeMessageForCode(attempt.ErrorCode),
                    Exception = ConversionFailureClassifier.SafeMessageForCode(attempt.ErrorCode)
                })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static void AppendAttempt(BsonDocument document, ConversionAttemptDiagnostic diagnostic)
    {
        var history = ReadAttemptHistory(document);
        AppendAttempt(history, diagnostic);
        document["attemptHistory"] = JsonSerializer.Serialize(history, JsonOptions);
    }

    private static void AppendAttempt(List<ConversionAttemptDiagnostic> history, ConversionAttemptDiagnostic diagnostic) => history.Add(diagnostic);

    private static ConversionAttemptDiagnostic CreateAttemptDiagnostic(int attempt, string errorCode, Exception exception, bool retryable) =>
        new(
            attempt,
            DateTimeOffset.UtcNow,
            errorCode,
            exception.GetType().Name,
            ConversionFailureClassifier.SafeMessageForCode(errorCode),
            ConversionFailureClassifier.SafeMessageForCode(errorCode),
            retryable);
}
