namespace InactivePDF.Api;

public static class ApiKeyScopes
{
    public const string ConvertSubmit = "convert:submit";
    public const string JobsRead = "jobs:read";
    public const string JobsCancel = "jobs:cancel";
    public const string OutputsRead = "outputs:read";
    public const string ProfilesRead = "profiles:read";
    public const string WatermarksRead = "watermarks:read";
    public const string UsageRead = "usage:read";
    public const string AdminRead = "admin:read";
    public const string AdminWrite = "admin:write";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ConvertSubmit, JobsRead, JobsCancel, OutputsRead, ProfilesRead,
        WatermarksRead, UsageRead, AdminRead, AdminWrite
    };

    public static IReadOnlyList<string> Default { get; } =
    [ConvertSubmit, JobsRead, OutputsRead, ProfilesRead, WatermarksRead];

    public static string[] Normalize(IReadOnlyList<string>? scopes)
    {
        var selected = scopes is null ? Default : scopes;
        if (selected.Count == 0) throw new ArgumentException("At least one API-key scope is required.", nameof(scopes));

        var normalized = selected
            .Select(scope => scope?.Trim() ?? string.Empty)
            .Where(scope => scope.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var unsupported = normalized.FirstOrDefault(scope => !All.Contains(scope));
        if (unsupported is not null)
            throw new ArgumentException($"Unsupported API-key scope '{unsupported}'.", nameof(scopes));
        return normalized;
    }
}

public sealed record ApiKeyLimits(
    int RequestsPerMinute = 60,
    int ConcurrentConversions = 2,
    int MaximumFilesPerRequest = 50,
    long MaximumRequestBytes = 512L * 1024 * 1024,
    long MaximumFileBytes = 256L * 1024 * 1024,
    long MaximumOutputBytes = 2L * 1024 * 1024 * 1024,
    long DailyInputBytes = 0,
    int JobRetentionDays = 0,
    int MaximumQueuedJobs = 256)
{
    public static ApiKeyLimits Default { get; } = new();

    public void Validate()
    {
        if (RequestsPerMinute is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(RequestsPerMinute), "Requests per minute must be between 1 and 1,000,000.");
        if (ConcurrentConversions is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(ConcurrentConversions), "Concurrent conversions must be between 1 and 10,000.");
        if (MaximumFilesPerRequest is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(MaximumFilesPerRequest), "Maximum files per request must be between 1 and 10,000.");
        if (MaximumRequestBytes is < 1) throw new ArgumentOutOfRangeException(nameof(MaximumRequestBytes), "Maximum request bytes must be positive.");
        if (MaximumFileBytes is < 1 || MaximumFileBytes > MaximumRequestBytes) throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes), "Maximum file bytes must be positive and no greater than maximum request bytes.");
        if (MaximumOutputBytes is < 1) throw new ArgumentOutOfRangeException(nameof(MaximumOutputBytes), "Maximum output bytes must be positive.");
        if (DailyInputBytes is < 0) throw new ArgumentOutOfRangeException(nameof(DailyInputBytes), "Daily input bytes cannot be negative; zero disables the daily quota.");
        if (JobRetentionDays is < 0 or > 36_500) throw new ArgumentOutOfRangeException(nameof(JobRetentionDays), "Job retention days must be zero or between 1 and 36,500.");
        if (MaximumQueuedJobs is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(MaximumQueuedJobs), "Maximum queued jobs must be between 1 and 1,000,000.");
    }
}

public sealed record ApiKeyCreateRequest(
    string? Name,
    IReadOnlyList<string>? Scopes = null,
    DateTimeOffset? ExpiresAt = null,
    ApiKeyLimits? Limits = null);

public sealed record ApiKeyRotateRequest(
    IReadOnlyList<string>? Scopes = null,
    DateTimeOffset? ExpiresAt = null,
    ApiKeyLimits? Limits = null);

public sealed record ApiKeyMetadata(
    Guid Id,
    string Name,
    string Prefix,
    IReadOnlyList<string> Scopes,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    string? LastSourceIp,
    ApiKeyLimits Limits);

public sealed record ApiKeySecretResponse(ApiKeyMetadata Key, string Secret);

public sealed class ApiKeyNameConflictException(string name) : Exception($"An API key named '{name}' already exists.");
