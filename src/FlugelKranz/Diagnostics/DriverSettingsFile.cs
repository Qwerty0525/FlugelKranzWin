using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlugelKranz.Diagnostics;

/// <summary>
/// The driver's own <c>resources/settings/default.vrsettings</c>, which SteamVR reads when it
/// loads the driver. The app edits only the <c>driver_flugelkranz.driverAudit</c> flag there,
/// so the change applies on the next SteamVR start. An entry for the same key in the user's
/// steamvr.vrsettings wins over it; that file is only read, never written.
/// </summary>
public static class DriverSettingsFile
{
    public const string Section = "driver_flugelkranz";
    public const string DriverAuditKey = "driverAudit";
    /// <summary>What build-windows.ps1 ships (and what Reset restores). Off: turn it on to collect DriverAudit lines.</summary>
    public const bool ShippedDriverAudit = false;

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// The driver folder next to the app (the release layout, <c>driver/flugelkranz</c>), else a
    /// registered external driver named flugelkranz from openvrpaths.vrpath. Null when neither has one.
    /// </summary>
    public static string? Locate(string appDirectory, string? openVrPathsFile)
    {
        string local = SettingsPath(Path.Combine(appDirectory, "driver", "flugelkranz"));
        if (File.Exists(local)) return local;
        if (openVrPathsFile is null || !File.Exists(openVrPathsFile)) return null;
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(openVrPathsFile), documentOptions: ReadOptions);
            foreach (var entry in root?["external_drivers"]?.AsArray() ?? [])
            {
                if (entry?.GetValue<string>() is not { } folder ||
                    !string.Equals(Path.GetFileName(folder.TrimEnd('\\', '/')), "flugelkranz", StringComparison.OrdinalIgnoreCase))
                    continue;
                string candidate = SettingsPath(folder);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception) { }
        return null;
    }

    private static string SettingsPath(string driverFolder) =>
        Path.Combine(driverFolder, "resources", "settings", "default.vrsettings");

    /// <summary>The flag in a vrsettings file, or null when the file, section or key is missing.</summary>
    public static bool? ReadDriverAudit(string path)
    {
        if (!File.Exists(path)) return null;
        var node = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions)?[Section]?[DriverAuditKey];
        return node is JsonValue value && value.TryGetValue(out bool flag) ? flag : null;
    }

    /// <summary>Sets the flag and keeps every other key; written through a temporary file.</summary>
    public static void WriteDriverAudit(string path, bool enabled)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject
            ?? throw new InvalidDataException($"{path} is not a JSON object.");
        if (root[Section] is not JsonObject section)
            root[Section] = section = new JsonObject();
        section[DriverAuditKey] = enabled;
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
        File.Move(temporary, path, true);
    }
}
