namespace InactivePDF.Domain.Models;

public sealed record ConversionRequest(
    string CorrelationId,
    ConversionOperation Operation,
    IReadOnlyList<DocumentInput> Inputs,
    ConversionOptions Options);
