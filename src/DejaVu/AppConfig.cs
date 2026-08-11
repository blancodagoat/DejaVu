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
    public int? ClipCapGB { get; set; }
    public string? CaptureTarget { get; set; }
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
    public static readonly int[] ClipCapChoices = [0, 10, 25, 50];

    public int BufferMinutes { get; set; } = 5;

    public Quality Quality { get; set; } = Quality.High;

    public int Fps { get; set; } = 60;

    public HotkeyBinding SaveHotkey { get; set; } = HotkeyBinding.DefaultSave;

    public string SaveRoot { get; set; } = AppInfo.DefaultSaveRoot;

    public bool ShowIndicator { get; set; } = true;

    public bool SystemAudio { get; set; } = true;

    /// <summary>Rolling cap on the saved-clips folder in GB; 0 means never delete.</summary>
    public int ClipCapGB { get; set; }

    /// <summary>"auto" follows the display of the active window; otherwise a GDI device
    /// name such as \\.\DISPLAY2 pins one monitor.</summary>
    public string CaptureTarget { get; set; } = "auto";

    /// <summary>True when no config existed on disk — the app's very first launch.</summary>
    public bool FirstRun { get; private set; }

    /// <summary>
    /// Constant-quality target (1–100) for the encoder's quality rate-control mode.
    /// Unlike a fixed bitrate this spends bits on action and almost nothing on static
    /// frames, which is what clip recording wants.
    /// </summary>
    public int QualityValue => Quality switch
    {
        Quality.Low => 50,
        Quality.Medium => 70,
        _ => 85,
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
            config.FirstRun = !File.Exists(AppInfo.ConfigPath);
            if (!config.FirstRun)
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
                    config.ClipCapGB = Math.Max(0, file.ClipCapGB ?? 0);
                    config.CaptureTarget = string.IsNullOrWhiteSpace(file.CaptureTarget)
                        ? "auto" : file.CaptureTarget;
                    rewrite |= file.ShowIndicator is null || file.SystemAudio is null
                        || file.ClipCapGB is null || file.CaptureTarget is null;
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
                ClipCapGB = ClipCapGB,
                CaptureTarget = CaptureTarget,
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
