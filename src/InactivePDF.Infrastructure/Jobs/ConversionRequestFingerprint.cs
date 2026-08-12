using System.Security.Cryptography;
using System.Text;
using InactivePDF.Application.Models;
using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Jobs;

/// <summary>
/// Creates a deterministic request identity without loading input documents into memory.
/// It includes operation/options, input metadata, and the SHA-256 digest of each input in order.
/// </summary>
public static class ConversionRequestFingerprint
{
    public static async Task<string> ComputeAsync(
        ConversionRequest request,
        IReadOnlyList<StoredInput> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(inputs);
        if (request.Inputs.Count != inputs.Count)
            throw new ArgumentException("Request metadata and stored inputs must have the same count.", nameof(inputs));

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(hash, request.Operation.ToString());
        Append(hash, request.Options.Profile);
        Append(hash, request.Options.PreserveExistingPdf.ToString());
        for (var index = 0; index < inputs.Count; index++)
        {
            var metadata = request.Inputs[index];
            Append(hash, metadata.FileName);
            Append(hash, metadata.ContentType);
            Append(hash, inputs[index].LengthBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await AppendFileAsync(hash, inputs[index].Path, cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task AppendFileAsync(IncrementalHash hash, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            hash.AppendData(buffer, 0, read);
        }
    }

    private static void Append(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
        hash.AppendData(BitConverter.GetBytes(bytes.Length));
        hash.AppendData(bytes);
    }
}
