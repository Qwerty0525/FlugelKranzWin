using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FlugelKranz.Core;
using FlugelKranz.Localization;

namespace FlugelKranz.ViewModels;

internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly BuildProfile profile;

    /// <param name="profile">Decides defaults for settings a config does not have yet (see <see cref="BuildProfiles"/>).</param>
    public SettingsStore(string? path = null, BuildProfile profile = BuildProfile.Release)
    {
        Path = path ?? ResolvePath();
        this.profile = profile;
    }

    public string Path { get; }

    /// <summary>Settings for a new config under this build profile.</summary>
    public FlugelKranzSettings NewDefaults => FlugelKranzSettings.Default with { Compass = profile.CompassDefaults() };

    // UI-only setting kept at the config root ("language"), outside the shared Core schema.
    // Missing or unknown values mean Japanese, so existing configs are unchanged.
    public string Language { get; set; } = UiStrings.DefaultLanguage;

    public FlugelKranzSettings Load()
    {
        try
        {
            if (!File.Exists(Path))
                return NewDefaults;

            string json = File.ReadAllText(Path);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("language", out var language) &&
                language.ValueKind == JsonValueKind.String)
                Language = UiStrings.Normalize(language.GetString());
            bool current = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("schemaVersion", out var version) &&
                version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt32(out int schemaVersion) &&
                schemaVersion == FlugelKranzSettings.CurrentSchemaVersion &&
                root.TryGetProperty("freeFlight", out _) &&
                root.TryGetProperty("infiniteWalking", out _);
            if (!current)
                return ResetToCurrentDefaults();

            var settings = JsonSerializer.Deserialize<FlugelKranzSettings>(json, JsonOptions)?.Normalized();
            if (settings is null)
                return ResetToCurrentDefaults();
            // A config without a saved compass on/off gets this build's default; a saved value wins.
            bool savedEnabled = root.TryGetProperty("compass", out var compass) &&
                compass.ValueKind == JsonValueKind.Object &&
                compass.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind is JsonValueKind.True or JsonValueKind.False;
            return savedEnabled ? settings
                : settings with { Compass = settings.Compass with { Enabled = profile.CompassDefaults().Enabled } };
        }
        catch (IOException)
        {
            return NewDefaults;
        }
        catch (JsonException)
        {
            return ResetToCurrentDefaults();
        }
    }

    public void Save(FlugelKranzSettings settings)
    {
        try
        {
            string? directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string temporaryPath = Path + ".tmp";
            var json = JsonSerializer.SerializeToNode(settings.Normalized(), JsonOptions)!.AsObject();
            json["language"] = UiStrings.Normalize(Language);
            File.WriteAllText(temporaryPath, json.ToJsonString(JsonOptions));
            File.Move(temporaryPath, Path, true);
        }
        catch (IOException)
        {
            // Configuration persistence must not stop tracking or the UI.
        }
        catch (UnauthorizedAccessException)
        {
            // Configuration persistence must not stop tracking or the UI.
        }
    }

    private FlugelKranzSettings ResetToCurrentDefaults()
    {
        var settings = NewDefaults;
        Save(settings);
        return settings;
    }

    private static string ResolvePath()
    {
        string? explicitPath = Environment.GetEnvironmentVariable("FLUGELKRANZ_CONFIG");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return System.IO.Path.GetFullPath(explicitPath);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        string configHome = string.IsNullOrWhiteSpace(xdgConfigHome)
            ? System.IO.Path.Combine(home, ".config")
            : xdgConfigHome;
        if (!System.IO.Path.IsPathRooted(configHome))
            configHome = System.IO.Path.Combine(home, configHome);

        return System.IO.Path.Combine(configHome, "FlugelKranz", "config.json");
    }
}
