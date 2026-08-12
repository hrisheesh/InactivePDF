namespace InactivePDF.Domain.Models;

public sealed record LibreOfficeOptions(
    string ExecutablePath,
    TimeSpan ConversionTimeout,
    string? AdditionalArguments = null)
{
    public static LibreOfficeOptions Default { get; } = new(ResolveDefaultPath(), ResolveTimeout(), ResolveAdditionalArguments());

    private static string ResolveDefaultPath() =>
        Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_PATH") ??
        (OperatingSystem.IsWindows() ? @"C:\Program Files\LibreOffice\program\soffice.exe" : "soffice");

    private static TimeSpan ResolveTimeout() =>
        int.TryParse(Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_TIMEOUT_SECONDS"), out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : TimeSpan.FromSeconds(180);

    private static string? ResolveAdditionalArguments() =>
        Environment.GetEnvironmentVariable("INACTIVEPDF_LIBREOFFICE_ADDITIONAL_ARGUMENTS");
}
