using System.Text.Json;
using System.Text.Json.Serialization;

namespace DejaVu;

internal enum Quality
{
    Low,
    Medium,
    High,
}

/// <summary>The on-disk shape of config.json.</summary>
internal sealed class ConfigFile
{
    public int? BufferMinutes { get; set; }
    public string? Quality { get; set; }
    public int? Fps { get; set; }
    public string? SaveHotkey { get; set; }
    public string? SaveRoot { get; set; }
    public bool? ShowIndicator { get; set; }
    public bool? SystemAudio { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigFile))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

internal sealed class AppConfig
{
    public static readonly int[] BufferChoices = [5, 10, 15, 20, 25];
    public static readonly int[] FpsChoices = [30, 60];

    public int BufferMinutes { get; set; } = 5;

    public Quality Quality { get; set; } = Quality.High;

    public int Fps { get; set; } = 60;

    public HotkeyBinding SaveHotkey { get; set; } = HotkeyBinding.DefaultSave;

    public string SaveRoot { get; set; } = AppInfo.DefaultSaveRoot;

    public bool ShowIndicator { get; set; } = true;

    public bool SystemAudio { get; set; } = true;

    /// <summary>Encoder bitrate in bits/second. Flat per preset; the encoder spends it as needed.</summary>
    public int Bitrate => Quality switch
    {
        Quality.Low => 8_000_000,
        Quality.Medium => 15_000_000,
        _ => 25_000_000,
    };

    /// <summary>
    /// Never throws. Anything unreadable or malformed collapses to defaults, and the
    /// file is rewritten so the next launch starts from something valid.
    /// </summary>
    public static AppConfig Load()
    {
        var config = new AppConfig();
        bool rewrite = true;

        try
        {
            if (File.Exists(AppInfo.ConfigPath))
            {
                var json = File.ReadAllText(AppInfo.ConfigPath);
                var file = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.ConfigFile);
                if (file is not null)
                {
                    rewrite = false;

                    if (file.BufferMinutes is int minutes && BufferChoices.Contains(minutes))
                    {
                        config.BufferMinutes = minutes;
                    }
                    else
                    {
                        rewrite = true;
                    }

                    if (Enum.TryParse(file.Quality, ignoreCase: true, out Quality quality))
                    {
                        config.Quality = quality;
                    }
                    else
                    {
                        rewrite = true;
                    }

                    if (file.Fps is int fps && FpsChoices.Contains(fps))
                    {
                        config.Fps = fps;
                    }
                    else
                    {
                        rewrite = true;
                    }

                    if (HotkeyBinding.TryParse(file.SaveHotkey, out var hotkey))
                    {
                        config.SaveHotkey = hotkey;
                    }
                    else
                    {
                        rewrite = true;
                    }

                    if (!string.IsNullOrWhiteSpace(file.SaveRoot))
                    {
                        config.SaveRoot = file.SaveRoot;
                    }
                    else
                    {
                        rewrite = true;
                    }

                    config.ShowIndicator = file.ShowIndicator ?? true;
                    config.SystemAudio = file.SystemAudio ?? true;
                    rewrite |= file.ShowIndicator is null || file.SystemAudio is null;
                }
            }
        }
        catch
        {
            // Defaults it is.
        }

        if (rewrite)
        {
            config.Save();
        }

        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppInfo.DataDirectory);
            var file = new ConfigFile
            {
                BufferMinutes = BufferMinutes,
                Quality = Quality.ToString(),
                Fps = Fps,
                SaveHotkey = SaveHotkey.ToString(),
                SaveRoot = SaveRoot,
                ShowIndicator = ShowIndicator,
                SystemAudio = SystemAudio,
            };

            File.WriteAllText(
                AppInfo.ConfigPath, JsonSerializer.Serialize(file, ConfigJsonContext.Default.ConfigFile));
        }
        catch
        {
            // A failed save costs the user their settings on next launch, nothing worse.
        }
    }
}
