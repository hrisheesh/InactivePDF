using InactivePDF.Domain.Contracts;

namespace InactivePDF.Application.Policies;

public sealed class FileNamePolicy : IFileNamePolicy
{
    public string ValidateAndNormalize(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var normalized = fileName.Trim();
        if (normalized is "." or ".." || Path.GetFileName(normalized) != normalized)
        {
            throw new ArgumentException("The filename must be a single safe filename.", nameof(fileName));
        }

        if (normalized.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException("The filename contains invalid characters.", nameof(fileName));
        }

        return normalized;
    }
}
