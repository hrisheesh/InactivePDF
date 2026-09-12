using System.Text.Json;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

/// <summary>Durable operator audit trail containing request metadata only, never secrets or document contents.</summary>
public sealed class AuditEventStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly object gate = new();
    private readonly string path;
    private readonly LinkedList<AuditEvent> recent = new();
    public string? RecordingError { get; private set; }

    public AuditEventStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        this.path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(this.path) ?? throw new ArgumentException("The audit path must have a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        EnsureSafePath();
        LoadRecent();
    }

    public void Record(HttpContext context)
    {
        var identity = ApiAuthentication.GetIdentity(context);
        var isApi = context.Request.Path.StartsWithSegments("/v1", StringComparison.OrdinalIgnoreCase);
        var isLive = context.Request.Path.StartsWithSegments("/v1/admin/live", StringComparison.OrdinalIgnoreCase);
        var shouldRecord = isApi && !isLive && (context.Response.StatusCode is >= 400 || context.Request.Method is not "GET" and not "HEAD");
        if (!shouldRecord) return;

        var audit = new AuditEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow, context.Request.Method.ToUpperInvariant(), context.Request.Path.Value ?? "/",
            context.Response.StatusCode, identity?.Kind.ToString() ?? "Anonymous", identity?.ApiKeyId,
            context.TraceIdentifier, context.Connection.RemoteIpAddress?.ToString());
        lock (gate)
        {
            try
            {
                EnsureSafePath();
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 16 * 1024, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, audit, JsonOptions);
                    stream.WriteByte((byte)'\n');
                    stream.Flush(true);
                }
                recent.AddFirst(audit);
                while (recent.Count > 200) recent.RemoveLast();
                RecordingError = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { RecordingError = "Audit events could not be recorded: " + error.GetType().Name; }
        }
    }

    public IReadOnlyList<AuditEvent> Recent(int limit = 100)
    {
        lock (gate) return recent.Take(Math.Clamp(limit, 1, 1_000)).ToArray();
    }

    private void LoadRecent()
    {
        if (!File.Exists(path)) return;
        try
        {
            EnsureSafePath();
            foreach (var line in File.ReadLines(path).Reverse().Take(200).Reverse())
            {
                try
                {
                    var value = JsonSerializer.Deserialize<AuditEvent>(line, JsonOptions);
                    if (value is not null) recent.AddFirst(value);
                }
                catch (JsonException) { }
            }
        }
        catch (IOException) { RecordingError = "Audit events could not be loaded."; }
        catch (UnauthorizedAccessException) { RecordingError = "Audit events could not be loaded."; }
    }

    private void EnsureSafePath()
    {
        var directory = Path.GetDirectoryName(path)!;
        WorkspacePathSecurity.EnsureSafeChain(directory, directory);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new UnauthorizedAccessException("The audit store cannot be a symbolic link or reparse point.");
    }
}

public sealed record AuditEvent(
    Guid Id, DateTimeOffset Timestamp, string Method, string Path, int StatusCode,
    string Identity, Guid? ApiKeyId, string RequestId, string? SourceIp);
