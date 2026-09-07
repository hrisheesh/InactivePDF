namespace InactivePDF.Domain.Models;

public sealed record LibreOfficeOptions(
    string ExecutablePath,
    TimeSpan ConversionTimeout,
    string? AdditionalArguments = null,
    string? SharedProfilePath = null)
{
    public static LibreOfficeOptions Default { get; } = new(ResolveDefaultPath(), ResolveTimeout(), ResolveAdditionalArguments(), ResolveSharedProfilePath());

    private static string ResolveDefaultPath() =>
        Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_PATH") ??
        (OperatingSystem.IsWindows() ? @"C:\Program Files\LibreOffice\program\soffice.exe" : "soffice");

    private static TimeSpan ResolveTimeout() =>
        int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(180);

    private static string? ResolveAdditionalArguments() =>
        Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_ADDITIONAL_ARGUMENTS");

    private static string? ResolveSharedProfilePath() =>
        Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE") is { Length: > 0 } path
            ? Path.GetFullPath(path)
            : null;
}
