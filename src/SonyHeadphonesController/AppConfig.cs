using System.IO;
using System.Text.Json;

namespace SonyCtrl;

/// <summary>
/// App configuration persisted to %APPDATA%\SonyHeadphonesController\config.json.
/// Holds the user-selected target earbuds.
/// </summary>
public sealed class AppConfig
{
    public string? TargetName { get; set; }
    public ulong? TargetAddress { get; set; }

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonyHeadphonesController");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
    };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOpts);
                if (cfg is not null) return cfg;
            }
        }
        catch
        {
            // Corrupt config: fall through to defaults (user re-picks the device).
        }
        return new AppConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        // Write-then-rename: a crash mid-write leaves the previous config intact instead of a
        // truncated JSON file, which Load would silently discard (forcing a device re-pick).
        string temp = ConfigPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(temp, ConfigPath, overwrite: true);
    }
}
