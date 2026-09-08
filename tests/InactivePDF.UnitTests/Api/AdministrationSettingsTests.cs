using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Api;
using InactivePDF.Infrastructure.Configuration;

namespace InactivePDF.UnitTests.Api;

public sealed class AdministrationSettingsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "inactivepdf-admin-" + Guid.NewGuid().ToString("N"));
    private readonly string path;
    public AdministrationSettingsTests()
    {
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "settings.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new InactivePdfSettings()));
    }
    private AdministrationSettingsStore Store() => new(path, new(), ["INACTIVEPDF_WORKER_COUNT"]);
    private static JsonObject Read(AdministrationSettingsStore store) => JsonSerializer.SerializeToNode(store.Read())!.AsObject();

    [Fact]
    public void ServiceProfilesSurviveRecreationAndRejectInvalidReplacement()
    {
        var workspace = new InactivePDF.Infrastructure.Resources.WorkspaceOptions { RootPath = directory };
        var store = new ServiceProfileStore(workspace);
        var config = JsonSerializer.SerializeToNode(new InactivePdfSettings())!.AsObject();
        store.Save("premium", config);
        var reloaded = new ServiceProfileStore(workspace);
        Assert.True(JsonNode.DeepEquals(config, reloaded.Read("premium")));
        config["Performance"]!["MaximumParallelWorkers"] = 0;
        Assert.Throws<InvalidDataException>(()=>reloaded.Save("premium",config));
        Assert.Equal(2, reloaded.Read("premium")!["Performance"]!["MaximumParallelWorkers"]!.GetValue<int>());
        Assert.Empty(Directory.GetFiles(Path.Combine(directory,"service-profiles"), "*.tmp"));
        Assert.Throws<ArgumentException>(()=>reloaded.Save("../escape",JsonSerializer.SerializeToNode(new InactivePdfSettings())!.AsObject()));
    }

    [Fact]
    public void SavesCompleteConfigurationAndRequiresRestart()
    {
        var store = Store(); var read = Read(store); var settings = read["settings"]!.DeepClone().AsObject();
        settings["Workers"]!["Count"] = 3;
        store.Save(settings, read["revision"]!.GetValue<string>());
        Assert.Equal(3, InactivePdfSettings.Load(path).Workers.Count);
        Assert.True(Read(store)["restartRequired"]!.GetValue<bool>());
        Assert.Equal(1, Read(store)["startupSettings"]!["Workers"]!["Count"]!.GetValue<int>());
    }
    [Fact]
    public void RejectsStaleSaveWithoutOverwritingNewConfiguration()
    {
        var store = Store(); var read = Read(store); var settings = read["settings"]!.DeepClone().AsObject();
        settings["Workers"]!["Count"] = 2;
        store.Save(settings, read["revision"]!.GetValue<string>());
        settings["Workers"]!["Count"] = 4;
        Assert.Throws<SettingsConflictException>(() => store.Save(settings, read["revision"]!.GetValue<string>()));
        Assert.Equal(2, InactivePdfSettings.Load(path).Workers.Count);
    }
    [Theory]
    [InlineData("invalid")]
    [InlineData("null")]
    [InlineData("missing")]
    public void RejectsInvalidOrIncompleteSettingsAndPreservesFile(string kind)
    {
        var before = File.ReadAllText(path); var store = Store(); var read = Read(store); var settings = read["settings"]!.DeepClone().AsObject();
        if (kind == "invalid") settings["Workers"]!["Count"] = 0;
        if (kind == "null") settings["Workers"] = null;
        if (kind == "missing") settings.Remove("Workers");
        Assert.Throws<InvalidDataException>(() => store.Save(settings, read["revision"]!.GetValue<string>()));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(directory));
    }
    [Fact]
    public void DetectsExternalEditsAndPreservesReferenceMetadata()
    {
        var store = Store(); var read = Read(store);
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document["CurrentDefaultsReference"] = new JsonObject { ["note"] = "deployment metadata" };
        File.WriteAllText(path, document.ToJsonString());
        Assert.Throws<SettingsConflictException>(() => store.Save(read["settings"]!.AsObject(), read["revision"]!.GetValue<string>()));
        var latest = Read(store); store.Save(latest["settings"]!.AsObject(), latest["revision"]!.GetValue<string>());
        Assert.Contains("deployment metadata", File.ReadAllText(path));
    }
    public void Dispose() => Directory.Delete(directory, true);
}
