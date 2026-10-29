namespace Rosella.Compositor;

/// <summary>
/// Where XDG desktop files live: the data directories in priority order,
/// user and flatpak exports first, system last. The first existing file
/// for a candidate id wins.
/// </summary>
internal static class DesktopLocator
{
    /// <summary>The application directories, highest priority first.</summary>
    internal static IReadOnlyList<string> DefaultSearchDirs() =>
        [.. DefaultDataRoots().Select(root => Path.Combine(root, "applications"))];

    /// <summary>
    /// The XDG data roots the shell searches (icons included), in order:
    /// the user data root, the flatpak export shares, each $XDG_DATA_DIRS
    /// entry, then /usr/share.
    /// </summary>
    internal static IReadOnlyList<string> DefaultDataRoots()
    {
        var roots = new List<string>();
        void Add(string root)
        {
            if (!string.IsNullOrEmpty(root) && !roots.Contains(root))
            {
                roots.Add(root);
            }
        }

        // $XDG_DATA_HOME replaces ~/.local/share as the user data root (the
        // XDG spec): it is the user root, never searched in addition to it.
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add(string.IsNullOrEmpty(dataHome) ? Path.Combine(home, ".local", "share") : dataHome);
        Add(Path.Combine(home, ".local", "share", "flatpak", "exports", "share"));
        Add("/var/lib/flatpak/exports/share");

        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        var entries = string.IsNullOrEmpty(dataDirs) ? "/usr/local/share:/usr/share" : dataDirs;
        foreach (var root in entries.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            Add(root);
        }

        Add("/usr/share");
        return roots;
    }

    /// <summary>
    /// Finds the first existing desktop file: candidate ids in priority
    /// order, each searched across the directories in order.
    /// </summary>
    internal static string? FindDesktopFile(IReadOnlyList<string> candidateIds, IReadOnlyList<string> searchDirs)
    {
        foreach (var id in candidateIds)
        {
            foreach (var dir in searchDirs)
            {
                var path = Path.Combine(dir, id);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }
}
