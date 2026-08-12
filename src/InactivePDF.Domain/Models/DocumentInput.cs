namespace InactivePDF.Domain.Models;

public sealed record DocumentInput(
    string FileName,
    string ContentType,
    long Length);
