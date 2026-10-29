namespace Rosella.Compositor;

/// <summary>
/// The pinned taskbar launchers: one small static table, resolved against
/// real XDG desktop files at startup. Everything about an entry is data —
/// edit the table to change the bar.
/// </summary>
internal static class PinnedApps
{
    /// <summary>The bar's pinned apps, left to right, with their desktop-file candidates.</summary>
    internal static readonly (string Name, string[] DesktopIds)[] Table =
    [
        ("Google Chrome", ["google-chrome.desktop", "google-chrome-stable.desktop", "com.google.Chrome.desktop"]),
        ("Terminal", ["org.gnome.Console.desktop", "gnome-terminal.desktop", "org.gnome.Terminal.desktop"]),
        ("Files", ["org.gnome.Nautilus.desktop", "nautilus.desktop"]),
        ("Visual Studio Code", ["com.microsoft.VSCode.desktop", "code.desktop", "code-oss.desktop"]),
        ("Steam", ["steam.desktop", "com.valvesoftware.Steam.desktop"]),
        ("Resources", ["org.gnome.World.Resources.desktop", "resources.desktop"]),
    ];

    /// <summary>The icon decode size: 2x the on-bar icon box, for crispness.</summary>
    internal const int IconDecodeSize = (Shell.BarHeight - 12) * 2;

    /// <summary>
    /// Resolves the table against the live system: apps without a desktop
    /// file (or with an unusable one) are skipped with a log line, and the
    /// bar shows only what resolved.
    /// </summary>
    internal static PinnedLauncher[] Resolve()
    {
        var searchDirs = DesktopLocator.DefaultSearchDirs();
        var dataRoots = DesktopLocator.DefaultDataRoots();
        var launchers = new List<PinnedLauncher>();
        foreach (var (fallbackName, desktopIds) in Table)
        {
            var path = DesktopLocator.FindDesktopFile(desktopIds, searchDirs);
            if (path is null)
            {
                Console.WriteLine($"pinned: {fallbackName}: no desktop file among [{string.Join(", ", desktopIds)}]; skipped");
                continue;
            }

            var entry = DesktopEntry.ParseFile(path);
            if (entry is null || entry.IsHidden)
            {
                Console.WriteLine($"pinned: {fallbackName}: {path} is hidden or unusable; skipped");
                continue;
            }

            var argv = DesktopEntry.ParseExec(entry.Exec);
            if (argv is null)
            {
                Console.WriteLine($"pinned: {fallbackName}: {path} has no usable Exec; skipped");
                continue;
            }

            var name = string.IsNullOrWhiteSpace(entry.Name) ? fallbackName : entry.Name.Trim();
            var iconPath = IconResolver.Resolve(entry.Icon, dataRoots);
            if (iconPath is null && !string.IsNullOrWhiteSpace(entry.Icon))
            {
                Console.WriteLine($"pinned: {fallbackName}: icon {entry.Icon} not found; using text tile");
            }

            var icon = iconPath is null ? null : IconLoader.Load(iconPath, IconDecodeSize);
            launchers.Add(new PinnedLauncher(
                name,
                Path.GetFileName(path),
                PinnedMatch.MatchKeysFor(Path.GetFileName(path), entry.StartupWMClass),
                argv,
                iconPath,
                icon));
        }

        return [.. launchers];
    }
}

/// <summary>Matching between a launcher and the windows that belong to it.</summary>
internal static class PinnedMatch
{
    /// <summary>
    /// The app-id keys a launcher's windows may show: the StartupWMClass,
    /// the desktop id sans ".desktop", and the last dot-separated segment
    /// of that id (app_id "google-chrome" matches google-chrome.desktop;
    /// app_id "nautilus" matches org.gnome.Nautilus.desktop).
    /// </summary>
    internal static string[] MatchKeysFor(string desktopId, string? startupWmClass)
    {
        var id = desktopId.EndsWith(".desktop", StringComparison.Ordinal)
            ? desktopId[..^".desktop".Length]
            : desktopId;
        var lastSegment = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;

        var keys = new List<string>();
        void Add(string key)
        {
            if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key.Trim(), StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(key.Trim());
            }
        }

        Add(startupWmClass ?? string.Empty);
        Add(id);
        Add(lastSegment);
        return [.. keys];
    }

    /// <summary>True when the toplevel's app_id names this launcher (case-insensitive).</summary>
    internal static bool Matches(string? appId, IReadOnlyList<string> matchKeys)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        foreach (var key in matchKeys)
        {
            if (string.Equals(appId.Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One pinned taskbar button: what it is called, what it runs, and which
/// windows it claims. Clicking focuses the newest matching window or
/// launches the app.
/// </summary>
internal sealed record PinnedLauncher(string Name, string DesktopId, string[] MatchKeys, string[] Argv, string? IconPath, IconImage? Icon)
{
    /// <summary>The fallback tile letter when no icon resolved: the Name's first letter.</summary>
    internal char TileLetter => Name.Length == 0 ? '?' : char.ToUpperInvariant(Name[0]);

    /// <summary>Focus-or-launch: matching windows win over spawning.</summary>
    internal void Click(IWindowRegistry windows, IReadOnlyList<int>? heldKeys = null)
    {
        if (windows.MostRecentMatching(MatchKeys) is { } match)
        {
            windows.RaiseAndFocus(match, heldKeys ?? []);
        }
        else
        {
            windows.Launch(Argv);
        }
    }
}
