#if ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

/// <summary>Resolves and decodes running-window icons off the scanout thread.</summary>
internal sealed class TaskbarIcons(IReadOnlyList<string>? dataRoots = null)
{
    private const int CacheSize = 128;
    private readonly Dictionary<(string Id, int Size), Task<IconImage?>> _icons = [];

    internal IconImage? Get(string appId, int size, Scene scene)
    {
        appId = appId.Trim();
        var key = (appId, size);
        if (!_icons.TryGetValue(key, out var loading))
        {
            if (appId.Length is 0 or > 256 || appId.Any(character => !char.IsAsciiLetterOrDigit(character)
                && character is not ('.' or '_' or '-')))
            {
                return null;
            }

            if (_icons.Count == CacheSize)
            {
                var completed = _icons.FirstOrDefault(entry => entry.Value.IsCompleted);
                if (completed.Value is null)
                {
                    return null;
                }

                _icons.Remove(completed.Key);
            }

            loading = Task.Run(() => Load(appId, size, dataRoots ?? DesktopLocator.DefaultDataRoots()));
            _icons.Add(key, loading);
            _ = loading.ContinueWith(result =>
            {
                if (result.Result is not null)
                {
                    scene.MarkDirty();
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }

        return loading.IsCompletedSuccessfully ? loading.Result : null;
    }

    private static IconImage? Load(string appId, int size, IReadOnlyList<string> roots)
    {
        if (!IconLoader.Available)
        {
            return null;
        }

        var name = appId.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? appId[..^8] : appId;
        var directories = roots.Select(root => Path.Combine(root, "applications")).ToArray();
        var desktopId = name + ".desktop";
        var desktop = DesktopLocator.FindDesktopFile([desktopId, desktopId.ToLowerInvariant()], directories);
        var entry = desktop is null ? null : DesktopEntry.ParseFile(desktop);
        var path = IconResolver.Resolve(entry?.Icon, roots) ?? IconResolver.Resolve(name, roots);
        if (path is null && desktop is null)
        {
            entry = FindByWindowClass(name, desktopId, directories);
            path = IconResolver.Resolve(entry?.Icon, roots);
        }

        return path is null ? null : IconLoader.Load(path, size);
    }

    private static DesktopEntry? FindByWindowClass(string appId, string desktopId, IReadOnlyList<string> directories)
    {
        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.desktop"))
                {
                    var entry = DesktopEntry.ParseFile(file);
                    if (entry is not null && (entry.Id.Equals(desktopId, StringComparison.OrdinalIgnoreCase) ||
                        entry.StartupWMClass.Equals(appId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return entry;
                    }
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // XDG search paths may include absent or unreadable directories.
            }
        }

        return null;
    }
}
