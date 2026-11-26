namespace Rosella.Compositor;

internal sealed record DashboardApp(string Id, string Name, string Icon)
{
    internal static DashboardApp[] Installed(IReadOnlyList<string> searchDirs)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var apps = new List<DashboardApp>();
        foreach (var directory in searchDirs)
        {
            try
            {
                foreach (var path in Directory.EnumerateFiles(directory, "*.desktop"))
                {
                    var id = Path.GetFileName(path);
                    if (PinnedConfiguration.IsValidDesktopId(id) && seen.Add(id) &&
                        DesktopEntry.ParseFile(path) is { IsHidden: false } entry &&
                        DesktopEntry.ParseExec(entry.Exec) is { Length: > 0 })
                    {
                        var name = string.IsNullOrWhiteSpace(entry.Name) ? id[..^8] : entry.Name.Trim();
                        apps.Add(new(id, name, entry.Icon));
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // One missing application directory should not hide the others.
            }
        }

        return [.. apps.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(app => app.Id, StringComparer.Ordinal)];
    }
}
