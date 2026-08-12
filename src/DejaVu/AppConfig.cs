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
    public string? IndicatorStyle { get; set; }
    public bool? SystemAudio { get; set; }
    public int? ClipCapGB { get; set; }
    public string? CaptureTarget { get; set; }
    public string[]? AudioExclude { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ConfigFile))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}

internal sealed class AppConfig
{
    public static readonly int[] BufferChoices = [5, 10, 15, 20, 25];
    public static readonly int[] ClipCapChoices = [0, 10, 25, 50];

    /// <summary>
    /// 30 and 60 always; higher standard steps only up to what some attached display can
    /// actually show (with 1 Hz tolerance for panels reporting 59/143). Computed once at
    /// startup — a hot-plugged faster monitor is picked up on the next launch.
    /// </summary>
    public static readonly int[] FpsChoices = BuildFpsChoices(Native.MaxDisplayRefresh());

    public static int[] BuildFpsChoices(int maxRefresh) =>
        new[] { 30, 60, 90, 120, 144, 165, 240 }
            .Where(f => f <= 60 || f <= maxRefresh + 1)
            .ToArray();

    public int BufferMinutes { get; set; } = 5;

    public Quality Quality { get; set; } = Quality.High;

    public int Fps { get; set; } = 60;

    public HotkeyBinding SaveHotkey { get; set; } = HotkeyBinding.DefaultSave;

    public string SaveRoot { get; set; } = AppInfo.DefaultSaveRoot;

    public bool ShowIndicator { get; set; } = true;

    /// <summary>"dot" (the red dot) or "icon" (the app icon) for the corner indicator.</summary>
    public string IndicatorStyle { get; set; } = "dot";

    public bool SystemAudio { get; set; } = true;

    /// <summary>Rolling cap on the saved-clips folder in GB; 0 means never delete.</summary>
    public int ClipCapGB { get; set; }

    /// <summary>"auto" follows the display of the active window; otherwise a GDI device
    /// name such as \\.\DISPLAY2 pins one monitor.</summary>
    public string CaptureTarget { get; set; } = "auto";

    /// <summary>True when no config existed on disk — the app's very first launch.</summary>
    public bool FirstRun { get; private set; }

    public static readonly string[] DefaultAudioExclude = ["Discord", "DiscordCanary", "DiscordPTB", "Vesktop"];

    /// <summary>
    /// Process names whose audio stays out of replays. The first one found running has
    /// its whole process tree excluded from the loopback mix — voice chat, notification
    /// pings, everything. Empty list = capture the full system mix.
    /// </summary>
    public string[] AudioExclude { get; set; } = DefaultAudioExclude;

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
                    config.IndicatorStyle = file.IndicatorStyle is "dot" or "icon" ? file.IndicatorStyle : "dot";
                    config.SystemAudio = file.SystemAudio ?? true;
                    config.ClipCapGB = Math.Max(0, file.ClipCapGB ?? 0);
                    config.CaptureTarget = string.IsNullOrWhiteSpace(file.CaptureTarget)
                        ? "auto" : file.CaptureTarget;
                    if (file.AudioExclude is not null)
                    {
                        config.AudioExclude = file.AudioExclude;
                    }

                    rewrite |= file.ShowIndicator is null || file.IndicatorStyle is null || file.SystemAudio is null
                        || file.ClipCapGB is null || file.CaptureTarget is null
                        || file.AudioExclude is null;
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
                IndicatorStyle = IndicatorStyle,
                SystemAudio = SystemAudio,
                ClipCapGB = ClipCapGB,
                CaptureTarget = CaptureTarget,
                AudioExclude = AudioExclude,
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
