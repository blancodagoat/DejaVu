using System.Diagnostics;

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
    private ToolStripMenuItem? saveItem;
    private HotkeyDialog? hotkeyDialog;

    private readonly UpdateNotifier updateNotifier;

    private string? lastSaved;
    private string? pendingReport;
    private Action? pendingUpdate;
    private bool saving;

    public TrayContext(SingleInstance instance)
    {
        config = AppConfig.Load();
        buffer = new ReplayBuffer(config);
        buffer.Failed += error => OnUi(() => FailureBalloon("Recording failed", error, ToolTipIcon.Error));

        indicator = new RecordingIndicator();

        hotkeys = new HotkeyWindow();
        hotkeys.HotkeyPressed += _ => SaveReplay();
        hotkeys.ShowSettingsRequested += () =>
            Balloon(AppInfo.Name + " is already running", "Right-click the tray icon.", ToolTipIcon.Info);
        hotkeys.QuitRequested += ExitThread;
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
        tray.BalloonTipClicked += (_, _) =>
        {
            if (pendingUpdate is { } update)
            {
                pendingUpdate = null;
                update();
            }
            else if (pendingReport is { } report)
            {
                pendingReport = null;
                IssueReport.Open(report);
            }
            else
            {
                RevealLastSaved();
            }
        };

        updateNotifier = new UpdateNotifier(() => config.UpdateNotify, (version, url) => OnUi(() =>
        {
            AnnounceUpdate(version, url);
        }));

        // Force handle creation so background threads can marshal onto the UI thread
        // through it even while the indicator is hidden.
        _ = indicator.Handle;
        indicator.UseIcon = config.IndicatorStyle == "icon";
        indicator.TargetDevice = config.CaptureTarget == "auto" ? null : config.CaptureTarget;
        if (config.ShowIndicator)
        {
            indicator.Show();
        }

        // A replay tool that is not running records nothing, so starting with Windows is
        // the default. The tray toggle still turns it off for good. Later launches
        // re-point the entry at wherever the exe lives now, so a moved or re-downloaded
        // exe does not silently break autostart.
        if (config.FirstRun)
        {
            StartupRegistry.TrySet(true);
        }
        else
        {
            StartupRegistry.Repair();
        }

        // Recovery first, off the UI thread — stitching a crashed session's segments can
        // take a moment and buffering must not restart on top of them. Time-bounded:
        // a hung recovery must never leave the app sitting dead in the tray. Guarded for
        // the same reason: a throw out of this Task.Run used to skip buffer.Start()
        // silently — a healthy-looking tray icon recording nothing, forever.
        Task.Run(() =>
        {
            string? recovered = null;
            bool timedOut = false;
            try
            {
                (recovered, timedOut) = buffer.RecoverWithTimeout(TimeSpan.FromSeconds(30));
            }
            catch (Exception ex)
            {
                AppLog.Write("startup recovery failed: " + ex.Message);
                OnUi(() => FailureBalloon("Recovery failed", ex.Message, ToolTipIcon.Warning));
            }

            buffer.Start();
            OnUi(SyncPauseUi);
            if (recovered is not null)
            {
                OnUi(() =>
                {
                    lastSaved = recovered;
                    Balloon("Replay recovered", $"Saved what the last session buffered: {Path.GetFileName(recovered)}", ToolTipIcon.Info);
                });
            }
            else if (timedOut)
            {
                OnUi(() => FailureBalloon(
                    "Recovery skipped",
                    "Last session's buffer could not be read; its files were parked in the buffer-quarantine folder.",
                    ToolTipIcon.Warning));
            }
        });

        // The corner dot is the only signal games don't suppress (Focus Assist eats
        // balloons exactly when a game is up). Poll the real state so it can never
        // assert "recording" over a buffer that gave up — or "paused" over one that
        // auto-recovered.
        var pulse = new System.Windows.Forms.Timer { Interval = 5000 };
        pulse.Tick += (_, _) => SyncPauseUi();
        pulse.Start();

        if (!hotkeyOk)
        {
            // Only the balloon said so before, and balloons are exactly what Focus Assist
            // eats — "the hotkey does nothing" arrived as a bug report with a clean log.
            AppLog.Write($"hotkey {config.SaveHotkey} could not be registered; another app holds it");
            Balloon(
                "Hotkey unavailable",
                $"{config.SaveHotkey} is taken by another app. Edit {AppInfo.ConfigPath} to rebind.",
                ToolTipIcon.Warning);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new DarkMenuRenderer() };

        saveItem = new ToolStripMenuItem("Save replay", null, (_, _) => SaveReplay())
        {
            ShortcutKeyDisplayString = config.SaveHotkey.ToString(),
            Font = Theme.Ui(9f, FontStyle.Bold),
        };

        menu.Items.Add(saveItem);
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
        // The simple path to the audioExclude config list. Toggling on resets any
        // hand-customized list back to the Discord defaults; the config key remains
        // the place for custom exclusions.
        menu.Items.Add(Toggle("Keep Discord out of clips", () => config.AudioExclude.Length > 0,
            v => { config.AudioExclude = v ? AppConfig.DefaultAudioExclude : []; buffer.Restart(); }));
        // Include-mode loopback: when a window is the capture target, record only its
        // process tree's audio. The clean answer to virtual mixers (SteelSeries Sonar)
        // re-rendering the mic into an exclude-mode mix. No effect on monitor capture.
        menu.Items.Add(Toggle("Captured app audio only", () => config.AppAudioOnly,
            v => { config.AppAudioOnly = v; buffer.Restart(); }));
        menu.Items.Add(Toggle("Save sound", () => config.SaveSound,
            v => config.SaveSound = v));
        menu.Items.Add(Choice("Corner indicator", new[] { "Off", "Red dot", "App icon" }, s => s,
            () => !config.ShowIndicator ? "Off" : config.IndicatorStyle == "icon" ? "App icon" : "Red dot",
            s =>
            {
                config.ShowIndicator = s != "Off";
                if (s != "Off")
                {
                    config.IndicatorStyle = s == "App icon" ? "icon" : "dot";
                }

                indicator.UseIcon = config.IndicatorStyle == "icon";
                if (config.ShowIndicator)
                {
                    indicator.Show();
                }
                else
                {
                    indicator.Hide();
                }
            }));

        var updates = new ToolStripMenuItem("Check for updates");
        updates.Click += async (_, _) =>
        {
            updates.Enabled = false;
            try
            {
                var newer = await UpdateCheck.FindNewer(UpdateCheck.Current);
                if (newer is { } found)
                {
                    AnnounceUpdate(found.Version, found.Url);
                }
                else
                {
                    Balloon("Up to date", $"You're on the newest release (v{UpdateCheck.Current}).", ToolTipIcon.None);
                }
            }
            catch (Exception ex)
            {
                // Proxies, TLS inspection, rate limits and plain blocks all land here;
                // the actual reason beats a generic "try again later".
                Balloon("Update check failed", "Couldn't reach GitHub: " + ex.Message, ToolTipIcon.Warning);
            }
            finally
            {
                updates.Enabled = true;
            }
        };
        menu.Items.Add(updates);

        var notify = Toggle("Notify about new versions", () => config.UpdateNotify,
            v => config.UpdateNotify = v);
        notify.ToolTipText = "Checks GitHub a few times a day, which means GitHub sees your IP. "
            + "Off (the default), the app never phones home.";
        menu.Items.Add(notify);

        // Off the UI thread: IsEnabled falls back to schtasks.exe with a 10 s wait when
        // the Run value is absent — exactly the case for anyone who turned autostart
        // off — and that used to freeze the menu on every open.
        var startup = new ToolStripMenuItem("Start with Windows");
        startup.Click += (_, _) => Task.Run(() =>
        {
            bool enable = !StartupRegistry.IsEnabled();
            StartupRegistry.TrySet(enable);
            OnUi(() => startup.Checked = StartupRegistry.IsEnabled());
        });
        menu.Opening += (_, _) => Task.Run(() =>
        {
            bool enabled = StartupRegistry.IsEnabled();
            OnUi(() => startup.Checked = enabled);
        });
        menu.Items.Add(startup);

        menu.Items.Add(new ToolStripMenuItem("Change save hotkey…", null, (_, _) => ShowHotkeyDialog()));

        // Hotkeys are not delivered while an elevated window has focus; running elevated
        // ourselves is the only bypass Windows allows.
        if (!Elevation.IsElevated)
        {
            menu.Items.Add(new ToolStripMenuItem("Restart as administrator", null, (_, _) =>
            {
                if (Elevation.TryRestartElevated())
                {
                    ExitThread();
                }
            })
            {
                ToolTipText = "Needed for the save hotkey to work while an admin app or game has focus.",
            });
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Open replays folder", null, (_, _) => OpenSaveFolder()));
        // Without this, a Controlled-Folder-Access-blocked Videos folder left users
        // with every save failing and hand-editing config.json as the only way out.
        menu.Items.Add(new ToolStripMenuItem("Change replays folder…", null, (_, _) => ChangeSaveFolder()));
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
            foreach (var (device, friendly) in Native.ListDisplays())
            {
                var deviceName = device;
                var item = new ToolStripMenuItem($"{friendly} ({device})")
                {
                    Checked = onDisplays && config.CaptureTarget == device,
                };
                item.Click += (_, _) => SetCaptureTarget(deviceName);
                parent.DropDownItems.Add(item);
            }
        }
        catch
        {
            // No display list is not fatal; auto still works.
        }

        try
        {
            var windows = Native.ListWindows().Take(12).ToList();
            if (windows.Count > 0)
            {
                parent.DropDownItems.Add(new ToolStripSeparator());
            }

            foreach (var (handle, title) in windows)
            {
                var hwnd = handle;
                var label = title.Length > 48 ? title[..48] + "…" : title;
                var item = new ToolStripMenuItem(label) { Checked = buffer.WindowTarget == hwnd };
                item.Click += (_, _) =>
                {
                    buffer.SetWindowTarget(hwnd);
                    // The dot belongs on the display being recorded — a game on the
                    // second monitor left it sitting on the primary, read as "no dot".
                    indicator.TargetDevice = Native.WindowDisplayDevice(hwnd);
                };
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
        // The dot belongs on the display being captured, not always the primary.
        indicator.TargetDevice = target == "auto" ? null : target;
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

    private void ShowHotkeyDialog()
    {
        if (hotkeyDialog is { IsDisposed: false })
        {
            hotkeyDialog.Activate();
            return;
        }

        hotkeyDialog = new HotkeyDialog(
            config.SaveHotkey,
            candidate =>
            {
                // Swap live: register the candidate; on refusal put the old one back so
                // the app is never left without a working hotkey.
                hotkeys.Unregister(HotkeyId.Save);
                if (hotkeys.Register(HotkeyId.Save, candidate))
                {
                    config.SaveHotkey = candidate;
                    config.Save();
                    if (saveItem is not null)
                    {
                        saveItem.ShortcutKeyDisplayString = candidate.ToString();
                    }

                    return true;
                }

                hotkeys.Register(HotkeyId.Save, config.SaveHotkey);
                return false;
            },
            recording =>
            {
                // While the field is armed, the global registration is released so the
                // current hotkey records instead of firing a save over the dialog.
                if (recording)
                {
                    hotkeys.Unregister(HotkeyId.Save);
                }
                else
                {
                    hotkeys.Register(HotkeyId.Save, config.SaveHotkey);
                }
            });
        hotkeyDialog.Show();
    }

    private void SaveReplay()
    {
        if (saving)
        {
            return;
        }

        saving = true;
        // Timestamped on the UI thread, where the hotkey lands: the gap between this line
        // and "saved" is the honest wall-clock a user feels, and a gap between the press
        // and THIS line is a stalled message pump rather than a slow save.
        AppLog.Write("save requested");
        // Resolved before anything else: once saving starts the foreground app is the
        // only trace of what the clip is about.
        var appName = SourceApp.Resolve();
        Task.Run(() =>
        {
            try
            {
                var path = buffer.Save(appName);
                AppLog.Write($"saved {Path.GetFileName(path)} ({new FileInfo(path).Length / 1024 / 1024} MB)");
                if (config.SaveSound)
                {
                    SaveChime.Success();
                }

                OnUi(() =>
                {
                    lastSaved = path;
                    Balloon("Replay saved", Path.GetFileName(path), ToolTipIcon.None);
                });
            }
            catch (Exception ex)
            {
                AppLog.Write("save failed: " + ex.Message);
                if (config.SaveSound)
                {
                    SaveChime.Failure();
                }

                OnUi(() => FailureBalloon("Save failed", ex.Message, ToolTipIcon.Error));
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
            buffer.Pause();
        }
        else
        {
            buffer.Start();
        }

        SyncPauseUi();
    }

    private void SyncPauseUi()
    {
        bool running = buffer.Running;
        pauseItem.Text = running ? "Pause buffering" : "Resume buffering";
        if (indicator.Buffering != running)
        {
            indicator.Buffering = running;
        }

        indicator.ReassertTopmost();
        tray.Text = running ? AppInfo.Name : AppInfo.Name + " — paused";
    }

    private void OpenSaveFolder()
    {
        Directory.CreateDirectory(config.SaveRoot);
        Process.Start(new ProcessStartInfo(config.SaveRoot) { UseShellExecute = true });
    }

    private void ChangeSaveFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Where saved replays go",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(config.SaveRoot) ? config.SaveRoot : "",
        };

        if (dialog.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            config.SaveRoot = dialog.SelectedPath;
            config.Save();
        }
    }

    private void RevealLastSaved()
    {
        if (lastSaved is not null && File.Exists(lastSaved))
        {
            Process.Start("explorer.exe", $"/select,\"{lastSaved}\"");
        }
    }

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        // Any newer balloon supersedes a pending report or update action, so a click on
        // "Replay saved" can never open an issue page or a release page.
        pendingReport = null;
        pendingUpdate = null;
        tray.ShowBalloonTip(4000, title, text, icon);
    }

    /// <summary>One update balloon, aimed at how this copy is actually managed: a scoop
    /// install must update through scoop (a raw exe would orphan the package), and a
    /// loose exe gets the download link plus a nudge toward being properly installed.</summary>
    private void AnnounceUpdate(Version version, string url)
    {
        if (ScoopInstall.Active)
        {
            Balloon("Update available",
                $"{AppInfo.Name} v{version} is out — click to update and restart now.",
                ToolTipIcon.Info);
            pendingUpdate = () =>
            {
                ScoopInstall.RunUpdateAndRelaunch();
                ExitThread();
            };
        }
        else
        {
            Balloon("Update available",
                $"{AppInfo.Name} v{version} is out — click to download. Tip: \"scoop install {AppInfo.Name.ToLowerInvariant()}\" makes updates one command.",
                ToolTipIcon.Info);
            pendingUpdate = () => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
    }

    /// <summary>
    /// A failure balloon whose click opens a prefilled GitHub issue in the browser —
    /// the user reviews and submits it there, or closes the tab; the app sends nothing.
    /// </summary>
    private void FailureBalloon(string title, string text, ToolTipIcon icon)
    {
        Balloon(title, text + " Click to report this on GitHub.", icon);
        pendingReport = title;
    }

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
            updateNotifier.Dispose();
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
