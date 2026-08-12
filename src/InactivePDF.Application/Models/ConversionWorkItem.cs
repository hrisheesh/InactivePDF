using InactivePDF.Domain.Models;

namespace InactivePDF.Application.Models;

public sealed record ConversionWorkItem(
    ConversionJob Job,
    ConversionRequest Request,
    IReadOnlyList<StoredInput> Inputs,
    string WorkspacePath,
    int Attempt = 0,
    string? LeaseOwner = null);
