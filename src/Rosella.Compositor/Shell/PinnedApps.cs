namespace Rosella.Compositor;

/// <summary>
/// The pinned taskbar launchers: defaults or a machine-specific ordered list,
/// resolved against real XDG desktop files at startup.
/// </summary>
internal static class PinnedApps
{
    /// <summary>The bar's pinned apps, desktop-file candidates and launch arguments.</summary>
    // Chromium honours the last Ozone switch. Append it even when a user
    // desktop entry forces X11: Rosella cannot host X11 clients. Chrome
    // profile selection happens at click
    // time, not at startup, since another session may start or stop Chrome.
    internal static readonly (string Name, string[] DesktopIds, string[] Arguments)[] Table =
    [
        ("Google Chrome", ["google-chrome.desktop", "google-chrome-stable.desktop", "com.google.Chrome.desktop"], ["--ozone-platform=wayland"]),
        ("Firefox", ["org.mozilla.firefox.desktop", "firefox.desktop"], []),
        ("Terminal", ["org.gnome.Ptyxis.desktop", "org.gnome.Console.desktop", "gnome-terminal.desktop", "org.gnome.Terminal.desktop"], []),
        ("Files", ["org.gnome.Nautilus.desktop", "nautilus.desktop"], []),
        ("Visual Studio Code", ["com.visualstudio.code.desktop", "com.microsoft.VSCode.desktop", "code.desktop", "code-oss.desktop"], []),
        ("Resources", ["net.nokyan.Resources.desktop", "org.gnome.World.Resources.desktop", "resources.desktop"], []),
        ("ChatGPT", ["chatgpt.desktop"], ["--ozone-platform=wayland"]),
        ("Blender", ["blender.desktop", "org.blender.Blender.desktop"], []),
        ("Commander", ["com.lamothe.Commander.desktop"], []),
    ];

    /// <summary>
    /// Resolves the table against the live system: apps without a desktop
    /// file (or with an unusable one) are skipped with a log line, and the
    /// bar shows only what resolved. Icons decode at exactly their
    /// on-screen pixel size (the caller knows the output scale), so the
    /// taskbar blit is 1:1 and crisp.
    /// </summary>
    internal static PinnedLauncher[] Resolve(int iconDecodeSize, string? display = null, bool secondary = false)
    {
        var path = PinnedConfiguration.SelectPath(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.MachineName);
        var configured = PinnedConfiguration.Load(path);
        return Resolve(iconDecodeSize, DesktopLocator.DefaultSearchDirs(), DesktopLocator.DefaultDataRoots(), configured, display, secondary);
    }

    internal static PinnedLauncher[] Resolve(int iconDecodeSize, IReadOnlyList<string> searchDirs, IReadOnlyList<string> dataRoots,
        string[][]? configured = null, string? display = null, bool secondary = false)
    {
        var groups = configured is null ? Table.Select(entry => entry.DesktopIds) : configured;
        var launchers = new List<PinnedLauncher>();
        foreach (var desktopIds in groups)
        {
            var fallbackName = Table.FirstOrDefault(entry => entry.DesktopIds.SequenceEqual(desktopIds)).Name ?? desktopIds[0];
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

            var isChrome = Table[0].DesktopIds.Contains(Path.GetFileName(path), StringComparer.Ordinal) || ChromeProfile.IsKnownChrome(argv);
            if (isChrome && !ChromeProfile.IsKnownChrome(argv) && !ChromeProfile.HasExplicitProfile(argv))
            {
                // A user entry can hide the exported Flatpak entry while its
                // opaque wrapper obscures the actual profile. Use only a real
                // Flatpak Chrome command for execution, retaining the user's
                // name, icon and matching metadata.
                foreach (var directory in searchDirs)
                {
                    var flatpakPath = Path.Combine(directory, "com.google.Chrome.desktop");
                    if (!File.Exists(flatpakPath))
                    {
                        continue;
                    }

                    var flatpakEntry = DesktopEntry.ParseFile(flatpakPath);
                    if (flatpakEntry is null || flatpakEntry.IsHidden)
                    {
                        continue;
                    }

                    var flatpakArgv = DesktopEntry.ParseExec(flatpakEntry.Exec);
                    if (flatpakArgv is not null && ChromeProfile.IsFlatpakChrome(flatpakArgv) && !ChromeProfile.HasExplicitProfile(flatpakArgv))
                    {
                        Console.WriteLine($"pinned: {fallbackName}: using {flatpakPath} for execution instead of opaque wrapper");
                        argv = flatpakArgv;
                        break;
                    }
                }
            }

            var desktopId = Path.GetFileName(path);
            var arguments = isChrome ? Table[0].Arguments
                : Table.FirstOrDefault(candidate => candidate.DesktopIds.Contains(desktopId, StringComparer.Ordinal)).Arguments;
            var name = string.IsNullOrWhiteSpace(entry.Name) ? fallbackName : entry.Name.Trim();
            var iconPath = IconResolver.Resolve(entry.Icon, dataRoots);
            if (iconPath is null && !string.IsNullOrWhiteSpace(entry.Icon))
            {
                Console.WriteLine($"pinned: {fallbackName}: icon {entry.Icon} not found; using text tile");
            }

            var icon = iconPath is null ? null : IconLoader.Load(iconPath, iconDecodeSize);
            launchers.Add(new PinnedLauncher(
                name,
                desktopId,
                PinnedMatch.MatchKeysFor(desktopId, entry.StartupWMClass),
                arguments is { Length: > 0 } ? [.. argv, .. arguments] : argv,
                iconPath,
                icon,
                isChrome ? argv => ChromeProfile.Prepare(argv, display, secondary) : desktopId switch
                {
                    // The shared bus activates file handlers on the primary
                    // desktop. Keep secondary Files windows on their own display.
                    "org.gnome.Nautilus.desktop" or "nautilus.desktop" when secondary => PreparePrivateBus,
                    "com.lamothe.Commander.desktop" => PreparePrivateBus,
                    _ => null,
                }));
        }

        return [.. launchers];
    }
    // These GApplication instances otherwise forward their activation to
    // whichever login registered the application ID on the shared user bus.
    private static string[] PreparePrivateBus(string[] argv) =>
        ["dbus-run-session", "--config-file=" + Path.Combine(AppContext.BaseDirectory, "private-app-bus.conf"), "--", .. argv];

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
internal sealed record PinnedLauncher(string Name, string DesktopId, string[] MatchKeys, string[] Argv, string? IconPath, IconImage? Icon, Func<string[], string[]>? PrepareLaunch = null)
{
    /// <summary>The fallback tile letter when no icon resolved: the Name's first letter.</summary>
    internal char TileLetter => Name.Length == 0 ? '?' : char.ToUpperInvariant(Name[0]);

    internal bool IsChatGpt => DesktopId.Equals("chatgpt.desktop", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Focus-or-launch: matching windows win over spawning. Returns the
    /// spawned process's pid when the click launched (null when it focused
    /// or nothing started), so the caller can spin the launcher until the
    /// window appears.
    /// </summary>
    internal int? Click(IWindowRegistry windows, IReadOnlyList<int>? heldKeys = null,
        Action<int?>? recovered = null, Action? recoveryFailed = null)
    {
        if (windows.MostRecentMatching(MatchKeys) is { } match)
        {
            windows.RaiseAndFocus(match, heldKeys ?? []);
            return null;
        }

        if (IsChatGpt && windows.TryRecoverChatGpt(MatchKeys, () =>
            {
                var pid = Launch(windows);
                recovered?.Invoke(pid);
            }, recoveryFailed))
        {
            return null;
        }

        return Launch(windows);
    }

    private int? Launch(IWindowRegistry windows)
    {
        var argv = PrepareLaunch is null ? Argv : PrepareLaunch(Argv);
        if (argv.Length > 0)
        {
            return windows.Launch(argv);
        }

        return null;
    }
}
