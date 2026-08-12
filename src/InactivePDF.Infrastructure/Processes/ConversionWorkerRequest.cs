using System.Text.Json.Serialization;
using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Processes;

/// <summary>
/// File-backed protocol exchanged between the API and the short-lived conversion worker.
/// Paths are used instead of document bytes so the API never has to materialize a complete
/// input or output document in memory.
/// </summary>
public sealed record ConversionWorkerRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ConversionOperation Operation,
    string OutputPath,
    IReadOnlyList<ConversionWorkerInput> Inputs,
    string Profile = "archive");

public sealed record ConversionWorkerInput(string Path, string FileName, string ContentType = "application/octet-stream");
