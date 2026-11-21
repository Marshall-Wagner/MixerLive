using System.Text.Json;
using System.Text.Json.Serialization;
using MixerLive.Core.Logging;

namespace MixerLive.Core.Model;

/// <summary>Loads and saves the active profile and app settings as JSON under %APPDATA%\MixerLive.</summary>
public static class ProfileStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string AppDataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MixerLive");

    public static string ProfilePath => Path.Combine(AppDataDirectory, "profile.json");
    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");

    /// <summary>
    /// When set (test and snapshot sessions), the active profile and settings are never written,
    /// so a second instance cannot overwrite what the running mixer saved. Explicit "Save as" still works.
    /// </summary>
    public static bool ReadOnly { get; set; }

    public static MixerProfile? LoadProfile(string? path = null)
    {
        path ??= ProfilePath;
        try
        {
            if (!File.Exists(path)) return null;
            var p = JsonSerializer.Deserialize<MixerProfile>(File.ReadAllText(path), JsonOptions);
            p?.Normalize();
            return p;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not load profile '{path}'", ex);
            return null;
        }
    }

    public static void SaveProfile(MixerProfile profile, string? path = null)
    {
        if (ReadOnly && path == null) return;
        path ??= ProfilePath;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(profile, JsonOptions));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error($"Could not save profile '{path}'", ex);
        }
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            Log.Error("Could not load settings", ex);
        }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
    {
        if (ReadOnly) return;
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    /// <summary>Deep copy via JSON round trip (profiles are small).</summary>
    public static MixerProfile Clone(MixerProfile profile)
    {
        var copy = JsonSerializer.Deserialize<MixerProfile>(JsonSerializer.Serialize(profile, JsonOptions), JsonOptions)!;
        copy.Normalize();
        return copy;
    }
}
