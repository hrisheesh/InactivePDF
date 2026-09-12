using InactivePDF.Domain.Models;
using InactivePDF.Infrastructure.Processes;

namespace InactivePDF.UnitTests.Processes;

public sealed class ConversionWorkerEnvironmentTests
{
    [Fact]
    public void SessionEnvironmentIsScopedToTheRequestedWindowsLease()
    {
        var environment = new Dictionary<string, string?>
        {
            ["INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE"] = "stale-profile",
            ["INACTIVEPDF_LIBREOFFICE_SESSION_PID"] = "123"
        };
        var request = new ConversionWorkerRequest(
            ConversionOperation.ConvertFile,
            "output.pdf",
            [new ConversionWorkerInput("input.docx", "input.docx")],
            LibreOfficeSessionProfilePath: "C:\\InactivePDF\\sessions\\slot-01\\profile",
            LibreOfficeSessionProcessId: 456);

        ConversionWorkerEnvironment.Apply(environment, request);

        Assert.Equal("C:\\InactivePDF\\sessions\\slot-01\\profile", environment["INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE"]);
        Assert.Equal("456", environment["INACTIVEPDF_LIBREOFFICE_SESSION_PID"]);
    }

    [Fact]
    public void NoSessionLeaseNeverInheritsAStaleSession()
    {
        var environment = new Dictionary<string, string?>
        {
            ["INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE"] = "stale-profile",
            ["INACTIVEPDF_LIBREOFFICE_SESSION_PID"] = "123"
        };

        ConversionWorkerEnvironment.Apply(environment, new ConversionWorkerRequest(
            ConversionOperation.ConvertFile,
            "output.pdf",
            [new ConversionWorkerInput("input.docx", "input.docx")]));

        Assert.False(environment.ContainsKey("INACTIVEPDF_LIBREOFFICE_SESSION_PROFILE"));
        Assert.False(environment.ContainsKey("INACTIVEPDF_LIBREOFFICE_SESSION_PID"));
    }
}
