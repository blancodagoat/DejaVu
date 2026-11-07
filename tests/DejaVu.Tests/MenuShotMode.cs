using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using DejaVu;
using Microsoft.Win32;

/// <summary>
/// "menushot [DejaVu.exe]": the tray's Audio devices submenu, checked by machine instead
/// of by eye (#11). Runs the Debug app with its menu-shot hook (DEJAVU_DEBUG_MENU_SHOT),
/// which opens the menu and the submenu, screenshots both and writes their layout, then
/// reads them back with Windows OCR and pixel checks: the default device first, checked,
/// greyed and labelled (default); every other active output listed by name; a ticked
/// device checked; an unplugged ticked one shown as "Disconnected device"; the note line
/// readable; and the submenu painted in the same colours as the main menu.
///
/// The app shares the real config.json, so it is backed up first, swapped for a test
/// config (save sound off, microphone off, clips to %TEMP%), and put back byte for byte
/// afterwards, hash-checked. The autostart Run value is checked unchanged.
/// </summary>
internal static class MenuShotMode
{
    private static int passed, failed;

    private static void Check(string name, bool ok, string? detail = null)
    {
        if (ok)
        {
            passed++;
            Console.WriteLine($"ok    {name}{(detail is null ? "" : "  (" + detail + ")")}");
            return;
        }

        failed++;
        Console.WriteLine($"FAIL  {name}{(detail is null ? "" : "  -> " + detail)}");
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Bogus = "{0.0.0.00000000}.{deadbeef-0000-4000-8000-000000000011}";

    public static int Run(string[] args)
    {
        var exe = args.Length > 1 ? args[1] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            @"..\..\..\..\..\..\..\src\DejaVu\bin\x64\Debug\net10.0-windows10.0.22621.0\win-x64\DejaVu.exe"));
        if (!File.Exists(exe))
        {
            Console.WriteLine($"menushot: no Debug app at {exe}; build src/DejaVu (Debug) first.");
            return 2;
        }

        if (System.Diagnostics.Process.GetProcessesByName("DejaVu").Length > 0)
        {
            Console.WriteLine("REFUSING menushot: DejaVu is running; quit it first.");
            return 2;
        }

        var devices = AudioLoopback.ListRenderDevices();
        var chat = devices.FirstOrDefault(d => !d.IsDefault && d.Name.Contains("Sonar - Chat", StringComparison.OrdinalIgnoreCase));
        if (chat.Id is null)
        {
            chat = devices.FirstOrDefault(d => !d.IsDefault);
        }

        if (chat.Id is null)
        {
            Console.WriteLine("SKIP menushot: needs a second output device.");
            return 0;
        }

        var outDir = Path.Combine(Path.GetTempPath(), $"DejaVu.MenuShot.{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(outDir);
        RunApp(exe, outDir, chat.Id);
        Analyze(outDir, devices, chat);
        Console.WriteLine($"      screenshots: {outDir}");
        Console.WriteLine($"{passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void RunApp(string exe, string outDir, string chatId)
    {
        var configPath = AppInfo.ConfigPath;
        byte[]? original = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        string? originalHash = original is null ? null : Convert.ToHexString(SHA256.HashData(original));
        var backup = Path.Combine(Path.GetTempPath(), $"dejavu_config_backup_{DateTime.Now:yyyyMMdd_HHmmss}.json");
        if (original is not null)
        {
            File.WriteAllBytes(backup, original);
        }

        object? runBefore = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(AppInfo.Name);
        Console.WriteLine($"      config.json sha256 {originalHash ?? "(none)"}, backup {backup}; Run value {(runBefore is null ? "absent" : "present")}");
        try
        {
            var test = new AppConfig
            {
                SaveRoot = Path.Combine(Path.GetTempPath(), "DejaVu.MenuShot.Clips"),
                SaveSound = false,
                Microphone = false,
                UpdateNotify = false,
                ShowIndicator = false,
                SystemAudio = true,
                ExtraAudioDevices = [chatId, Bogus],
            };
            File.WriteAllText(configPath, test.ToJson());

            var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            start.Environment["DEJAVU_DEBUG_MENU_SHOT"] = outDir;
            start.Environment["DEJAVU_BUFFER_DIR"] = Path.Combine(Path.GetTempPath(), "DejaVu.MenuShot.Buffer");
            using var app = System.Diagnostics.Process.Start(start)!;
            bool exited = app.WaitForExit(90_000);
            if (!exited)
            {
                app.Kill();
                app.WaitForExit(10_000);
            }

            Check("menushot: the app took its screenshots and exited by itself", exited && app.ExitCode == 0,
                exited ? $"exit {app.ExitCode}" : "killed after 90 s");
        }
        finally
        {
            if (original is null)
            {
                File.Delete(configPath);
            }
            else
            {
                File.WriteAllBytes(configPath, original);
            }

            string? nowHash = File.Exists(configPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(configPath))) : null;
            Check("menushot: config.json restored byte for byte", nowHash == originalHash, $"sha256 {nowHash}");
            object? runAfter = Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(AppInfo.Name);
            Check("menushot: autostart Run value unchanged", Equals(runAfter, runBefore),
                $"{(runAfter is null ? "absent" : "present")}");
            Check("menushot: no DejaVu process left", System.Diagnostics.Process.GetProcessesByName("DejaVu").Length == 0);
        }
    }

    private sealed record Item(string Strip, int Index, bool Separator, string Text, Rectangle Bounds, bool Checked, bool Enabled, bool Selected);

    private static void Analyze(string dir, List<AudioLoopback.RenderDevice> devices, AudioLoopback.RenderDevice chat)
    {
        var layoutPath = Path.Combine(dir, "layout.tsv");
        if (!File.Exists(layoutPath))
        {
            Check("menushot: layout written", false);
            return;
        }

        var lines = File.ReadAllLines(layoutPath);
        var theme = lines[0].Split('\t').Skip(1).Select(kv => kv.Split('=')).ToDictionary(kv => kv[0], kv => ColorTranslator.FromHtml(kv[1]));
        var strips = lines.Where(l => l.StartsWith("strip\t")).Select(l => l.Split('\t'))
            .ToDictionary(p => p[1], p => new Rectangle(int.Parse(p[2]), int.Parse(p[3]), int.Parse(p[4]), int.Parse(p[5])));
        var items = lines.Where(l => l.StartsWith("item\t")).Select(l => l.Split('\t')).Select(p => new Item(
            p[1], int.Parse(p[2]), p[3] == "separator", p[4],
            new Rectangle(int.Parse(p[5]), int.Parse(p[6]), int.Parse(p[7]), int.Parse(p[8])),
            bool.Parse(p[9]), bool.Parse(p[10]), bool.Parse(p[11]))).ToList();
        var sub = items.Where(i => i.Strip == "audio").ToList();
        var subItems = sub.Where(i => !i.Separator).ToList();

        // --- what the menu holds (the layout the app reported) ---
        var def = devices.Single(d => d.IsDefault);
        var first = subItems.FirstOrDefault();
        Check("layout: the default device comes first", first is not null && first.Text.StartsWith(def.Name), first?.Text);
        Check("layout: the default is labelled (default)", first?.Text.EndsWith(" (default)") == true, first?.Text);
        Check("layout: the default is checked and greyed (disabled)", first is { Checked: true, Enabled: false });
        foreach (var d in devices.Where(d => !d.IsDefault))
        {
            var row = subItems.FirstOrDefault(i => i.Text == d.Name);
            Check($"layout: {d.Name} listed by name", row is not null);
            if (row is not null)
            {
                Check($"layout: {d.Name} {(d.Id == chat.Id ? "ticked" : "unticked")}, clickable",
                    row.Checked == (d.Id == chat.Id) && row.Enabled);
            }
        }

        var disconnected = subItems.FirstOrDefault(i => i.Text == "Disconnected device");
        Check("layout: the unplugged ticked device shows as Disconnected device, ticked", disconnected is { Checked: true, Enabled: true });
        var note = subItems.LastOrDefault();
        Check("layout: the note line closes the list, not clickable",
            note is { Enabled: false } && note.Text == "Ticked devices are recorded whole, Discord included", note?.Text);
        Check("layout: a separator above the note", sub.Count > 1 && sub[^2].Separator);

        // --- what is on screen ---
        using var menuPng = new Bitmap(Path.Combine(dir, "menu.png"));
        using var subPng = new Bitmap(Path.Combine(dir, "audio.png"));
        var subRect = strips["audio"];
        var menuRect = strips["menu"];
        Rectangle Local(Item i, Rectangle strip) => new(i.Bounds.X - strip.X, i.Bounds.Y - strip.Y, i.Bounds.Width, i.Bounds.Height);

        var subBg = Mode(subPng, new Rectangle(0, 0, subPng.Width, subPng.Height));
        var menuBg = Mode(menuPng, new Rectangle(0, 0, menuPng.Width, menuPng.Height));
        Check("pixels: submenu background is the theme background", Near(subBg, theme["background"], 3), Hex(subBg));
        Check("pixels: submenu background matches the main menu's", Near(subBg, menuBg, 2), $"{Hex(subBg)} vs {Hex(menuBg)}");
        var subEdge = Mode(subPng, new Rectangle(0, 0, subPng.Width, 1));
        var menuEdge = Mode(menuPng, new Rectangle(0, 0, menuPng.Width, 1));
        Check("pixels: submenu border matches the main menu's", Near(subEdge, menuEdge, 3), $"{Hex(subEdge)} vs {Hex(menuEdge)}");

        // Text colour per row: the mean of the pixels that stand clearly off the background
        // in the text area (right of the check margin).
        // Text colour per row: the glyph pixels (clearly off the background) in the text
        // area right of the check margin. Ink is their mean; Core is the brightest 15 %,
        // the glyph colour itself rather than its anti-aliased edges.
        (Color Ink, int Count, Color Core) TextInk(Bitmap bmp, Rectangle row, Color bg)
        {
            var area = new Rectangle(row.X + 30, row.Y + 3, Math.Min(row.Width - 40, 300), row.Height - 6);
            var px = new List<Color>();
            for (int y = area.Top; y < area.Bottom; y++)
            {
                for (int x = area.Left; x < area.Right && x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (Distance(c, bg) > 60)
                    {
                        px.Add(c);
                    }
                }
            }

            if (px.Count == 0)
            {
                return (bg, 0, bg);
            }

            var core = px.OrderByDescending(c => Distance(c, bg)).Take(Math.Max(1, px.Count * 15 / 100));
            return (Average(px), px.Count, Average(core));
        }

        // Check margin: anything but background in the left 26 px of the row.
        int MarkPixels(Bitmap bmp, Rectangle row, Color bg)
        {
            int n = 0;
            for (int y = row.Top + 2; y < row.Bottom - 2; y++)
            {
                for (int x = row.Left + 2; x < row.Left + 26; x++)
                {
                    if (Distance(bmp.GetPixel(x, y), bg) > 40)
                    {
                        n++;
                    }
                }
            }

            return n;
        }

        var normalRows = subItems.Where(i => i.Enabled && i.Text != "Disconnected device").ToList();
        var normalInk = normalRows.Select(i => TextInk(subPng, Local(i, subRect), subBg)).Where(t => t.Count > 0).ToList();
        double normalLuma = normalInk.Count == 0 ? 0 : normalInk.Average(t => Luma(t.Ink));
        if (first is not null)
        {
            var (ink, _, _) = TextInk(subPng, Local(first, subRect), subBg);
            Check("pixels: the default row is greyed (dimmer than the clickable rows)", Luma(ink) < normalLuma * 0.75,
                $"default ink {Hex(ink)} luma {Luma(ink):F0}, clickable rows luma {normalLuma:F0}");
            Check("pixels: the default row shows a check mark", MarkPixels(subPng, Local(first, subRect), subBg) > 20);
        }

        foreach (var row in subItems.Where(i => i.Enabled))
        {
            int marks = MarkPixels(subPng, Local(row, subRect), subBg);
            Check($"pixels: {row.Text} {(row.Checked ? "shows" : "has no")} check mark", row.Checked ? marks > 20 : marks == 0, $"{marks} px");
        }

        // The clickable rows use the same ink as the main menu's rows.
        var menuRows = items.Where(i => i.Strip == "menu" && !i.Separator && i.Enabled && !i.Selected && i.Index > 1).ToList();
        var menuInk = menuRows.Select(i => TextInk(menuPng, Local(i, menuRect), menuBg)).Where(t => t.Count > 0).ToList();
        if (menuInk.Count > 0 && normalInk.Count > 0)
        {
            var m = Average(menuInk.Select(t => t.Ink)); var s = Average(normalInk.Select(t => t.Ink));
            Check("pixels: submenu text ink matches the main menu's", Near(m, s, 12), $"{Hex(s)} vs {Hex(m)}");
        }

        // The parent item stays highlighted with the theme's hover and accent while open.
        var parent = items.FirstOrDefault(i => i.Strip == "menu" && i.Text == "Audio devices");
        if (parent is not null)
        {
            var local = Local(parent, menuRect);
            var hover = Mode(menuPng, new Rectangle(local.X + 30, local.Y + 2, 6, local.Height - 4));
            Check("pixels: Audio devices row highlighted in the theme's hover colour", Near(hover, theme["hover"], 6), Hex(hover));
            var (_, _, core) = TextInk(menuPng, local, hover);
            Check("pixels: Audio devices row text in the theme's accent", Near(core, theme["accent"], 30), Hex(core));
        }

        if (note is not null)
        {
            var (_, _, core) = TextInk(subPng, Local(note, subRect), subBg);
            double contrast = Contrast(core, subBg);
            // WCAG AA: 4.5:1 for small text, judged on the glyph colour.
            Check("pixels: the note line has readable contrast (>= 4.5:1)", contrast >= 4.5,
                $"glyphs {Hex(core)} on {Hex(subBg)}: {contrast:F2}:1");
        }

        // --- OCR: the words a person would read ---
        var ocr = Ocr(Path.Combine(dir, "audio.png"));
        Console.WriteLine("      OCR of the submenu:");
        foreach (var l in ocr)
        {
            Console.WriteLine("        " + l);
        }

        string? Line(Item i)
        {
            // OCR lines come in reading order; match on the row by text similarity.
            return ocr.OrderByDescending(l => Similarity(Norm(l), Norm(i.Text))).FirstOrDefault();
        }

        if (first is not null)
        {
            // Check marks OCR as stray one- or two-letter lines; they are not rows.
            var top = ocr.FirstOrDefault(l => Norm(l).Length > 2) ?? "";
            Check("OCR: the first line is the default device, labelled (default)",
                Similarity(Norm(top), Norm(first.Text)) > 0.85 && Norm(top).Contains("default"), top);
        }

        foreach (var row in subItems.Skip(1))
        {
            var seen = Line(row) ?? "";
            Check($"OCR: reads \"{row.Text}\"", Similarity(Norm(seen), Norm(row.Text)) > 0.85, seen);
        }
    }

    private static List<string> Ocr(string png)
    {
        // OCR reads dark-on-light, larger text best: invert and scale 3x first.
        using var src = new Bitmap(png);
        using var big = new Bitmap(src.Width * 3, src.Height * 3);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, big.Width, big.Height);
        }

        for (int y = 0; y < big.Height; y++)
        {
            for (int x = 0; x < big.Width; x++)
            {
                var c = big.GetPixel(x, y);
                big.SetPixel(x, y, Color.FromArgb(255 - c.R, 255 - c.G, 255 - c.B));
            }
        }

        var prepared = Path.ChangeExtension(png, ".ocr.png");
        big.Save(prepared, ImageFormat.Png);

        var file = Windows.Storage.StorageFile.GetFileFromPathAsync(prepared).AsTask().GetAwaiter().GetResult();
        using var stream = file.OpenAsync(Windows.Storage.FileAccessMode.Read).AsTask().GetAwaiter().GetResult();
        var decoder = Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        using var bitmap = decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied)
            .AsTask().GetAwaiter().GetResult();
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
            ?? Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
        {
            Check("OCR: an OCR engine is available", false);
            return [];
        }

        var result = engine.RecognizeAsync(bitmap).AsTask().GetAwaiter().GetResult();
        return result.Lines.OrderBy(l => l.Words.Min(w => w.BoundingRect.Y)).Select(l => l.Text).ToList();
    }

    private static string Norm(string s) =>
        new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static double Similarity(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0)
        {
            return 1;
        }

        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
        }

        return 1 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
    }

    private static Color Mode(Bitmap bmp, Rectangle area)
    {
        var counts = new Dictionary<int, int>();
        for (int y = area.Top; y < area.Bottom && y < bmp.Height; y++)
        {
            for (int x = area.Left; x < area.Right && x < bmp.Width; x++)
            {
                int argb = bmp.GetPixel(x, y).ToArgb();
                counts[argb] = counts.GetValueOrDefault(argb) + 1;
            }
        }

        return Color.FromArgb(counts.MaxBy(kv => kv.Value).Key);
    }

    private static Color Average(IEnumerable<Color> colors)
    {
        var list = colors.ToList();
        return Color.FromArgb((int)list.Average(c => c.R), (int)list.Average(c => c.G), (int)list.Average(c => c.B));
    }

    private static double Distance(Color a, Color b) =>
        Math.Sqrt((a.R - b.R) * (a.R - b.R) + (a.G - b.G) * (a.G - b.G) + (a.B - b.B) * (a.B - b.B));

    private static bool Near(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance;

    private static double Luma(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

    private static double Contrast(Color a, Color b)
    {
        static double L(Color c)
        {
            static double Ch(int v)
            {
                double s = v / 255.0;
                return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
        }

        double la = L(a), lb = L(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static string Hex(Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";
}
