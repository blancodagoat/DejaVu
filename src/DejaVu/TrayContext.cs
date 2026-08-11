using System.Diagnostics;
using ScreenRecorderLib;

namespace DejaVu;

/// <summary>The tray icon is the whole UI: every setting lives in its context menu.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig config;
    private readonly ReplayBuffer buffer;
    private readonly HotkeyWindow hotkeys;
    private readonly SystemTheme theme;
    private readonly RecordingIndicator indicator;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem pauseItem;

    private string? lastSaved;
    private bool saving;

    public TrayContext(SingleInstance instance)
    {
        config = AppConfig.Load();
        buffer = new ReplayBuffer(config);
        buffer.Failed += error => OnUi(() => Balloon("Recording failed", error, ToolTipIcon.Error));

        indicator = new RecordingIndicator();

        hotkeys = new HotkeyWindow();
        hotkeys.HotkeyPressed += _ => SaveReplay();
        hotkeys.ShowSettingsRequested += () =>
            Balloon(AppInfo.Name + " is already running", "Right-click the tray icon.", ToolTipIcon.Info);
        instance.ListenForSignals(hotkeys.Handle);

        bool hotkeyOk = hotkeys.Register(HotkeyId.Save, config.SaveHotkey);

        theme = new SystemTheme();
        theme.Changed += () => tray!.Icon = TrayIcons.ForTaskbar(theme.LightTaskbar);

        pauseItem = new ToolStripMenuItem("Pause buffering", null, (_, _) => TogglePause());

        tray = new NotifyIcon
        {
            Icon = TrayIcons.ForTaskbar(theme.LightTaskbar),
            Text = AppInfo.Name,
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        tray.DoubleClick += (_, _) => OpenSaveFolder();
        tray.BalloonTipClicked += (_, _) => RevealLastSaved();

        // Force handle creation so background threads can marshal onto the UI thread
        // through it even while the indicator is hidden.
        _ = indicator.Handle;
        if (config.ShowIndicator)
        {
            indicator.Show();
        }

        // A replay tool that is not running records nothing, so starting with Windows is
        // the default. The tray toggle still turns it off for good.
        if (config.FirstRun)
        {
            StartupRegistry.TrySet(true);
        }

        // Recovery first, off the UI thread — stitching a crashed session's segments can
        // take a moment and buffering must not restart on top of them.
        Task.Run(() =>
        {
            var recovered = buffer.RecoverCrashedSession();
            buffer.Start();
            if (recovered is not null)
            {
                OnUi(() =>
                {
                    lastSaved = recovered;
                    Balloon("Replay recovered", $"Saved what the last session buffered: {Path.GetFileName(recovered)}", ToolTipIcon.Info);
                });
            }
        });

        if (!hotkeyOk)
        {
            Balloon(
                "Hotkey unavailable",
                $"{config.SaveHotkey} is taken by another app. Edit {AppInfo.ConfigPath} to rebind.",
                ToolTipIcon.Warning);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };

        var save = new ToolStripMenuItem("Save replay", null, (_, _) => SaveReplay())
        {
            ShortcutKeyDisplayString = config.SaveHotkey.ToString(),
            Font = Theme.Ui(9f, FontStyle.Bold),
        };

        menu.Items.Add(save);
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());

        var capture = new ToolStripMenuItem("Capture");
        capture.DropDown.Renderer = new DarkMenuRenderer();
        capture.DropDownOpening += (_, _) => RebuildCaptureMenu(capture);
        // Populated up front too, so the submenu arrow shows before the first open.
        RebuildCaptureMenu(capture);
        menu.Items.Add(capture);

        menu.Items.Add(Choice("Buffer length", AppConfig.BufferChoices, m => $"{m} minutes",
            () => config.BufferMinutes, m => config.BufferMinutes = m));
        menu.Items.Add(Choice("Quality", Enum.GetValues<Quality>(), q => q.ToString(),
            () => config.Quality, q => { config.Quality = q; buffer.Restart(); }));
        menu.Items.Add(Choice("Frame rate", AppConfig.FpsChoices, f => $"{f} fps",
            () => config.Fps, f => { config.Fps = f; buffer.Restart(); }));
        menu.Items.Add(Choice("Clip folder cap", AppConfig.ClipCapChoices,
            g => g == 0 ? "Off" : $"{g} GB",
            () => config.ClipCapGB, g => config.ClipCapGB = g));

        menu.Items.Add(Toggle("System audio", () => config.SystemAudio,
            v => { config.SystemAudio = v; buffer.Restart(); }));
        menu.Items.Add(Toggle("Corner indicator", () => config.ShowIndicator, v =>
        {
            config.ShowIndicator = v;
            if (v)
            {
                indicator.Show();
            }
            else
            {
                indicator.Hide();
            }
        }));

        var startup = new ToolStripMenuItem("Start with Windows");
        startup.Click += (_, _) => { StartupRegistry.TrySet(!StartupRegistry.IsEnabled()); };
        menu.Opening += (_, _) => startup.Checked = StartupRegistry.IsEnabled();
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open replays folder", null, (_, _) => OpenSaveFolder()));
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        return menu;
    }

    /// <summary>
    /// The capture picker: auto (follow the active window's display), a pinned monitor,
    /// or one specific window. Rebuilt on every open because displays and windows come
    /// and go. Window picks last for the session only — handles do not survive restarts.
    /// </summary>
    private void RebuildCaptureMenu(ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();

        bool onDisplays = buffer.WindowTarget == IntPtr.Zero;
        var auto = new ToolStripMenuItem("Auto (active display)")
        {
            Checked = onDisplays && config.CaptureTarget == "auto",
        };
        auto.Click += (_, _) => SetCaptureTarget("auto");
        parent.DropDownItems.Add(auto);

        try
        {
            foreach (var display in Recorder.GetDisplays())
            {
                var device = display.DeviceName;
                var item = new ToolStripMenuItem($"{display.FriendlyName} ({device})")
                {
                    Checked = onDisplays && config.CaptureTarget == device,
                };
                item.Click += (_, _) => SetCaptureTarget(device);
                parent.DropDownItems.Add(item);
            }
        }
        catch
        {
            // No display list is not fatal; auto still works.
        }

        try
        {
            var windows = Recorder.GetWindows()
                .Where(w => w.IsValidWindow() && !w.IsMinmimized() && !string.IsNullOrWhiteSpace(w.Title))
                .Take(12)
                .ToList();

            if (windows.Count > 0)
            {
                parent.DropDownItems.Add(new ToolStripSeparator());
            }

            foreach (var window in windows)
            {
                var handle = window.Handle;
                var title = window.Title.Length > 48 ? window.Title[..48] + "…" : window.Title;
                var item = new ToolStripMenuItem(title) { Checked = buffer.WindowTarget == handle };
                item.Click += (_, _) => buffer.SetWindowTarget(handle);
                parent.DropDownItems.Add(item);
            }
        }
        catch
        {
            // Same: the window list is a convenience.
        }
    }

    private void SetCaptureTarget(string target)
    {
        config.CaptureTarget = target;
        config.Save();
        if (buffer.WindowTarget != IntPtr.Zero)
        {
            buffer.SetWindowTarget(IntPtr.Zero);
        }
        else
        {
            buffer.Restart();
        }
    }

    /// <summary>A radio-checked submenu bound to one config value; changing it saves the config.</summary>
    private ToolStripMenuItem Choice<T>(
        string title, IEnumerable<T> values, Func<T, string> label, Func<T> get, Action<T> set)
        where T : notnull
    {
        var parent = new ToolStripMenuItem(title);
        foreach (var value in values)
        {
            var item = new ToolStripMenuItem(label(value)) { Tag = value };
            item.Click += (_, _) =>
            {
                set(value);
                config.Save();
            };
            parent.DropDownItems.Add(item);
        }

        parent.DropDownOpening += (_, _) =>
        {
            foreach (ToolStripMenuItem item in parent.DropDownItems)
            {
                item.Checked = Equals(item.Tag, get());
            }
        };

        parent.DropDown.Renderer = new DarkMenuRenderer();
        return parent;
    }

    private ToolStripMenuItem Toggle(string title, Func<bool> get, Action<bool> set)
    {
        var item = new ToolStripMenuItem(title) { Checked = get() };
        item.Click += (_, _) =>
        {
            set(!get());
            config.Save();
            item.Checked = get();
        };
        return item;
    }

    private void SaveReplay()
    {
        if (saving)
        {
            return;
        }

        saving = true;
        // Resolved before anything else: once saving starts the foreground app is the
        // only trace of what the clip is about.
        var appName = SourceApp.Resolve();
        Task.Run(() =>
        {
            try
            {
                var path = buffer.Save(appName);
                OnUi(() =>
                {
                    lastSaved = path;
                    Balloon("Replay saved", Path.GetFileName(path), ToolTipIcon.None);
                });
            }
            catch (Exception ex)
            {
                OnUi(() => Balloon("Save failed", ex.Message, ToolTipIcon.Error));
            }
            finally
            {
                saving = false;
            }
        });
    }

    private void TogglePause()
    {
        if (buffer.Running)
        {
            buffer.Stop();
        }
        else
        {
            buffer.Start();
        }

        pauseItem.Text = buffer.Running ? "Pause buffering" : "Resume buffering";
        indicator.Buffering = buffer.Running;
        tray.Text = buffer.Running ? AppInfo.Name : AppInfo.Name + " — paused";
    }

    private void OpenSaveFolder()
    {
        Directory.CreateDirectory(config.SaveRoot);
        Process.Start(new ProcessStartInfo(config.SaveRoot) { UseShellExecute = true });
    }

    private void RevealLastSaved()
    {
        if (lastSaved is not null && File.Exists(lastSaved))
        {
            Process.Start("explorer.exe", $"/select,\"{lastSaved}\"");
        }
    }

    private void Balloon(string title, string text, ToolTipIcon icon) =>
        tray.ShowBalloonTip(4000, title, text, icon);

    private void OnUi(Action action)
    {
        if (indicator.IsHandleCreated)
        {
            indicator.BeginInvoke(action);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tray.Visible = false;
            tray.Dispose();
            buffer.Dispose();
            hotkeys.Dispose();
            theme.Dispose();
            indicator.Dispose();
        }

        base.Dispose(disposing);
    }
}
