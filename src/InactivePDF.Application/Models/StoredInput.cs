namespace InactivePDF.Application.Models;

public sealed record StoredInput(string FileName, string Path, long LengthBytes, string ContentType = "application/octet-stream");
