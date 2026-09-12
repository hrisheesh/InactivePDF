using InactivePDF.Domain.Models;

namespace InactivePDF.Infrastructure.Processes;

internal static class ConversionWorkerEnvironment
{
    public const string SessionProfileVariable = "INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE";
    public const string SessionProcessIdVariable = "INACTIVEPDF_LIBREOFFICE_SESSION_PID";

    public static void Apply(IDictionary<string, string?> environment, ConversionWorkerRequest request)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(request);
        environment.Remove(SessionProfileVariable);
        environment.Remove(SessionProcessIdVariable);

        if (!string.IsNullOrWhiteSpace(request.LibreOfficeSessionProfilePath))
            environment[SessionProfileVariable] = request.LibreOfficeSessionProfilePath;
        if (request.LibreOfficeSessionProcessId is > 0)
            environment[SessionProcessIdVariable] = request.LibreOfficeSessionProcessId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}
