using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DejaVu;

/// <summary>
/// Cleans up after browser re-downloads: a fresh copy lands as "DejaVu (1).exe" next to
/// the stale one, and the folder fills with numbered duplicates the user launches at
/// random. On launch (first instance only — a running old copy holds the single-instance
/// mutex, and its file couldn't be deleted anyway) stale same-name siblings are deleted
/// and the running exe sheds its " (N)" suffix: a running exe can be renamed, just not
/// deleted. Siblings with a NEWER file version are left alone — autostart can launch the
/// old copy while a fresh download sits beside it, and deleting the upgrade would be the
/// one unforgivable outcome.
/// </summary>
internal static class SelfTidy
{
    private static readonly Regex CopySuffix = new(@" \(\d+\)$");

    /// <summary>"DejaVu (2)" → "DejaVu"; names without a copy suffix pass through.</summary>
    public static string StripCopySuffix(string nameWithoutExtension) =>
        CopySuffix.Replace(nameWithoutExtension, "");

    public static void Run()
    {
        try
        {
            var self = Environment.ProcessPath;
            var dir = self is null ? null : Path.GetDirectoryName(self);
            if (self is null || dir is null)
            {
                return;
            }

            var name = Path.GetFileNameWithoutExtension(self);
            var baseName = StripCopySuffix(name);
            var ourVersion = ReadVersion(self);

            foreach (var file in Directory.EnumerateFiles(dir, baseName + "*.exe"))
            {
                if (string.Equals(file, self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Exactly "base.exe" or "base (N).exe" — "DejaVu-self-contained.exe"
                // is a different artifact, not a duplicate of "DejaVu.exe".
                var other = Path.GetFileNameWithoutExtension(file);
                bool duplicate = other.Equals(baseName, StringComparison.OrdinalIgnoreCase)
                    || (CopySuffix.IsMatch(other)
                        && StripCopySuffix(other).Equals(baseName, StringComparison.OrdinalIgnoreCase));
                if (!duplicate || ReadVersion(file) > ourVersion)
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Locked by a scanner; the next launch retries.
                }
            }

            if (name != baseName)
            {
                var target = Path.Combine(dir, baseName + ".exe");
                if (!File.Exists(target))
                {
                    File.Move(self, target);
                    AppInfo.NoteRenamed(target);
                }
            }
        }
        catch
        {
            // Tidying is best-effort and must never block startup.
        }
    }

    private static Version ReadVersion(string path)
    {
        try
        {
            return Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var v)
                ? v
                : new Version(0, 0);
        }
        catch
        {
            return new Version(0, 0);
        }
    }
}
