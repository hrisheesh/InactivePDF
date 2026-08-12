namespace InactivePDF.Infrastructure.Processes;

internal static class LibreOfficeProfileProvisioner
{
    public static void Create(string profileDirectory)
    {
        var configDirectory = Path.Combine(profileDirectory, "user", "config");
        Directory.CreateDirectory(configDirectory);
        File.WriteAllText(Path.Combine(configDirectory, "registrymodifications.xcu"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <oor:items xmlns:oor="http://openoffice.org/2001/registry" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:install="http://openoffice.org/2004/installation" xmlns:prop="http://openoffice.org/2001/registry">
              <item oor:path="/org.openoffice.Office.Common/Security/Scripting">
                <prop oor:name="MacroSecurityLevel" oor:op="fuse"><value>3</value></prop>
              </item>
              <item oor:path="/org.openoffice.Office.Common/Security/Trusted">
                <prop oor:name="UntrustedSources" oor:op="fuse"><value>true</value></prop>
              </item>
              <item oor:path="/org.openoffice.Office.Common/Update">
                <prop oor:name="Enabled" oor:op="fuse"><value>false</value></prop>
              </item>
            </oor:items>
            """);
    }
}
