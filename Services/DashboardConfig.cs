using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KrakenEliteScreenManager.Services;

public enum DashboardMode { GifLoop, Dashboard, Coolant }

/// <summary>
/// What the service should display, persisted at ~/.config/kraken-elite-screen-manager/config.json.
/// The chosen GIF lives next to it as bg.gif. The GUI writes this; the service reads it.
///   GifLoop   - upload the GIF, firmware loops it (no overlay).
///   Dashboard - clock + temps, refreshed live via double-buffering.
///   Coolant   - the built-in NZXT liquid-temperature screen.
/// </summary>
public sealed class DashboardConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DashboardMode Mode { get; set; } = DashboardMode.Coolant;

    public int RefreshSeconds { get; set; } = 5; // Dashboard stat refresh cadence
    public int Brightness { get; set; } = 80;
    public int Rotation { get; set; } = 270; // panel mount; content is pre-rotated by this

    // --- storage --------------------------------------------------------------

    public static string Dir { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "kraken-elite-screen-manager");

    public static string ConfigFile => Path.Combine(Dir, "config.json");
    public static string GifFile => Path.Combine(Dir, "bg.gif");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static DashboardConfig Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
                return JsonSerializer.Deserialize<DashboardConfig>(File.ReadAllText(ConfigFile)) ?? new();
        }
        catch { /* fall back to defaults on any parse error */ }
        return new DashboardConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ConfigFile, JsonSerializer.Serialize(this, JsonOpts));
    }
}
