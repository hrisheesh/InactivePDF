using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InactivePDF.Infrastructure.Configuration;
using InactivePDF.Infrastructure.Resources;

namespace InactivePDF.Api;

/// <summary>Edits the startup file without mutating options owned by running workers.</summary>
public sealed class AdministrationSettingsStore
{
    private readonly object sync = new();
    private readonly string? path;
    private readonly JsonObject startup;
    private readonly string[] overrides;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public AdministrationSettingsStore(string? path, InactivePdfSettings startup, string[] overrides)
    {
        this.path = path;
        this.startup = JsonSerializer.SerializeToNode(startup, Json)!.AsObject();
        this.overrides = overrides;
    }

    public object Read()
    {
        lock (sync)
        {
            var settings = LoadDocument();
            var configured = JsonSerializer.SerializeToNode(settings.Deserialize<InactivePdfSettings>(Json), Json)!;
            return new { settings, startupSettings = startup, revision = Revision(settings), writable = path is not null,
                restartRequired = !JsonNode.DeepEquals(configured, startup), environmentOverrides = overrides };
        }
    }

    public object Save(JsonObject document, string revision)
    {
        lock (sync)
        {
            if (path is null) throw new InvalidOperationException("No startup settings file is configured. Set INACTIVEPDF_SETTINGS_PATH and restart the service.");
            if (Revision(LoadDocument()) != revision) throw new SettingsConflictException();
            // Require the full typed shape. Missing sections and explicit null objects must never reset settings silently.
            RequireShape(startup, document, "");
            var typed = document.Deserialize<InactivePdfSettings>(Json) ?? throw new InvalidDataException("Settings are required.");
            InactivePdfSettings.Validate(typed);
            if (typed.Api.MaximumFileBytes > typed.Api.MaximumRequestBytes)
                throw new InvalidDataException("Api.MaximumFileBytes cannot exceed Api.MaximumRequestBytes.");
            var directory = Path.GetDirectoryName(path)!;
            WorkspacePathSecurity.EnsureSafeChain(path, directory);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    var bytes = Encoding.UTF8.GetBytes(document.ToJsonString(Json));
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                WorkspacePathSecurity.EnsureSafeChain(path, directory);
                try { File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true); }
                catch (PlatformNotSupportedException) { File.Move(temporary, path, true); }
                catch (IOException) { File.Move(temporary, path, true); }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return Read();
        }
    }

    private JsonObject LoadDocument()
    {
        if (path is null) return startup.DeepClone().AsObject();
        var source = JsonNode.Parse(File.ReadAllText(path), documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!.AsObject();
        // Add defaults for fields omitted by older deployments; preserve reference metadata and unknown fields.
        MergeDefaults(source, startup);
        return source;
    }

    private static void MergeDefaults(JsonObject target, JsonObject defaults)
    {
        foreach (var (key, value) in defaults)
            if (!target.ContainsKey(key)) target[key] = value?.DeepClone();
            else if (value is JsonObject nested && target[key] is JsonObject existing) MergeDefaults(existing, nested);
    }

    private static void RequireShape(JsonObject template, JsonObject candidate, string prefix)
    {
        foreach (var (key, value) in template)
        {
            if (!candidate.ContainsKey(key)) throw new InvalidDataException($"{prefix}{key} is required.");
            if (value is JsonObject nested)
            {
                if (candidate[key] is not JsonObject child) throw new InvalidDataException($"{prefix}{key} must be an object.");
                RequireShape(nested, child, prefix + key + ".");
            }
        }
    }

    private static string Revision(JsonObject settings) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings.ToJsonString())));
}

public sealed class SettingsConflictException : Exception;
