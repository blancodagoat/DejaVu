using System.Diagnostics;
using System.Text;

namespace DejaVu;

internal static class SourceApp
{
    public const string Unknown = "unknown";

    /// <summary>
    /// The characters Windows rejects in a file name. Path.GetInvalidFileNameChars is
    /// platform-dependent — it returns only NUL and '/' on non-Windows hosts — so the set
    /// is pinned here to keep the produced name identical wherever this code is exercised.
    /// </summary>
    private static readonly char[] InvalidNameChars =
        Path.GetInvalidFileNameChars()
            .Concat(new[] { '"', '<', '>', '|', ':', '*', '?', '\\', '/' })
            .Distinct()
            .ToArray();

    /// <summary>
    /// Must run as the first statement of a hotkey handler. Once the region overlay is
    /// shown it becomes the foreground window and the real source is unrecoverable.
    /// </summary>
    public static string Resolve()
    {
        try
        {
            var hwnd = Native.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return Unknown;
            }

            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
            {
                return Unknown;
            }

            using var process = Process.GetProcessById((int)pid);
            return Sanitise(process.ProcessName);
        }
        catch
        {
            return Unknown;
        }
    }

    /// <summary>Lowercase, no path, no .exe, filename-safe.</summary>
    public static string Sanitise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Unknown;
        }

        var value = name.Trim();

        // GetFileName throws on nothing, but a caller-supplied path may still carry
        // separators the invalid-char pass below would otherwise turn into dashes.
        int slash = value.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        value = value.ToLowerInvariant();

        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            bool bad = c == ' ' || c == '.' || Array.IndexOf(InvalidNameChars, c) >= 0;
            char mapped = bad ? '-' : c;
            if (mapped == '-' && sb.Length > 0 && sb[^1] == '-')
            {
                continue;
            }

            sb.Append(mapped);
        }

        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? Unknown : result;
    }
}
