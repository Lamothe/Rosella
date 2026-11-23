using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace Rosella.Compositor;

/// <summary>
/// The cursor theme name and size GTK clients use: GSettings' desktop
/// interface keys when a session bus answers, else the GTK settings.ini
/// files, else the <c>default</c> theme at 24 — GTK's own fallbacks (an
/// unset <c>gtk-cursor-theme-name</c> is NULL, which reaches libXcursor as
/// "default"). Rosella does not link GTK, so the GSettings value is queried
/// through `gsettings` once at startup; a missing or unresponsive bus falls
/// through to the files instead of delaying the session.
/// </summary>
internal static class CursorSettings
{
    internal const string DefaultTheme = "default";
    internal const int DefaultSize = 24;

    /// <summary>
    /// The chosen theme, its size, and the ini theme when GSettings named
    /// another one — the caller retries it if the chosen theme is not
    /// installed (a schema default can name a theme the machine lacks).
    /// </summary>
    internal readonly record struct Choice(string Theme, int Size, string? Fallback);

    /// <summary>
    /// Resolves the settings. <paramref name="query"/> reads one GSettings
    /// key ("cursor-theme" or "cursor-size") and may return null when no
    /// session bus answers; tests inject it rather than spawn gsettings.
    /// </summary>
    internal static Choice Resolve(
        Func<string, string?>? query = null,
        string? home = null,
        string? configHome = null,
        IReadOnlyList<string>? systemSettings = null)
    {
        query ??= QueryGSettings;
        var (iniTheme, iniSize) = ReadIniFiles(home, configHome, systemSettings);

        // One failed query means there is no usable bus: skip the second
        // rather than paying the deadline twice at session start.
        var themeText = query("cursor-theme");
        var sizeText = themeText is null ? null : query("cursor-size");
        var theme = Unquote(themeText) ?? iniTheme ?? DefaultTheme;

        var size = int.TryParse(sizeText ?? iniSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            && parsed is > 0 and <= 512
            ? parsed
            : DefaultSize;

        var fallback = iniTheme is not null && !string.Equals(iniTheme, theme, StringComparison.Ordinal)
            ? iniTheme
            : null;
        return new Choice(theme, size, fallback);
    }

    /// <summary>
    /// `gsettings get org.gnome.desktop.interface &lt;key&gt;`. A Wayland
    /// session's D-Bus may not be up yet at compositor start, and gsettings
    /// can block trying to reach it: the child gets a short deadline and is
    /// killed, after which the caller's ini/default fallback applies.
    /// stderr is inherited, not piped: an undrained pipe would turn a chatty
    /// child into the timeout path.
    /// </summary>
    private static string? QueryGSettings(string key)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gsettings")
            {
                ArgumentList = { "get", "org.gnome.desktop.interface", key },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });
            if (process is null)
            {
                return null;
            }

            if (!process.WaitForExit(750))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Exited between the timeout and the kill.
                }

                return null;
            }

            if (process.ExitCode != 0)
            {
                return null;
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            return output.Length == 0 ? null : output;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>gsettings prints strings as 'quoted text'.</summary>
    private static string? Unquote(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '\'' && trimmed[^1] == '\'')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Length == 0 ? null : trimmed;
    }

    /// <summary>
    /// GTK's file-backed settings: the user's gtk-3.0/gtk-4.0 settings.ini
    /// (3.0 first, because Chrome reads GTK 3), then the system copies. The
    /// system list is a parameter so tests never read the host's /etc.
    /// </summary>
    private static (string? Theme, string? Size) ReadIniFiles(
        string? home,
        string? configHome,
        IReadOnlyList<string>? systemSettings)
    {
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        configHome ??= Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } configured
            ? configured
            : Path.Combine(home, ".config");

        var paths = new List<string>
        {
            Path.Combine(configHome, "gtk-3.0", "settings.ini"),
            Path.Combine(configHome, "gtk-4.0", "settings.ini"),
        };
        paths.AddRange(systemSettings ?? ["/etc/gtk-3.0/settings.ini", "/etc/gtk-4.0/settings.ini"]);

        string? theme = null;
        string? size = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!seen.Add(path) || !File.Exists(path))
            {
                continue;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed[0] is '#' or ';' or '[')
                {
                    continue;
                }

                var separator = trimmed.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = trimmed[..separator].Trim();
                var value = trimmed[(separator + 1)..].Trim();
                if (key.Equals("gtk-cursor-theme-name", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    theme ??= value;
                }
                else if (key.Equals("gtk-cursor-theme-size", StringComparison.OrdinalIgnoreCase) && value.Length > 0)
                {
                    size ??= value;
                }
            }
        }

        return (theme, size);
    }
}