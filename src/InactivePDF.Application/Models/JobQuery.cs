using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Models;

public sealed record JobQuery(
    ConversionJobState? State = null,
    string? Source = null,
    string? Format = null,
    DateTimeOffset? CreatedAfter = null,
    DateTimeOffset? CreatedBefore = null,
    string? Cursor = null,
    int Limit = 50,
    Guid? OwnerApiKeyId = null);

public sealed record JobPage(
    IReadOnlyList<JobStatus> Jobs,
    string? NextCursor);
