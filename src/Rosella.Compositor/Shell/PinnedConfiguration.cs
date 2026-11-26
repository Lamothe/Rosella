using System.Text;
using System.Text.Json;

namespace Rosella.Compositor;

/// <summary>Ordered desktop-file IDs from the user's machine-specific launcher list.</summary>
internal static class PinnedConfiguration
{
    private const int MaxBytes = 64 * 1024;
    private const int MaxGroups = 32;
    private const int MaxAlternatives = 16;
    private const int MaxIdLength = 255;

    internal static string? SelectPath(string home, string? configHome, string? machineName)
    {
        var (machineFile, sharedFile) = Paths(home, configHome, machineName);
        return SelectPath(machineFile, sharedFile);
    }

    private static (string? Machine, string Shared) Paths(string home, string? configHome, string? machineName)
    {
        var root = !string.IsNullOrEmpty(configHome) && Path.IsPathFullyQualified(configHome)
            ? configHome : Path.Combine(home, ".config");
        var directory = Path.Combine(root, "rosella");
        var machineFile = !string.IsNullOrEmpty(machineName) && machineName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? Path.Combine(directory, $"launchers.{machineName}.json") : null;
        return (machineFile, Path.Combine(directory, "launchers.json"));
    }

    private static string? SelectPath(string? machineFile, string sharedFile) =>
        File.Exists(machineFile) ? machineFile : File.Exists(sharedFile) ? sharedFile : null;

    internal static string[][]? Load(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
        {
            return Read(path).Select(entry => entry.DesktopIds).ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine($"pinned: cannot load {path}: {exception.Message}; using defaults");
            return null;
        }
    }

    internal static void Remove(string desktopId) => Remove(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.MachineName, desktopId);

    internal static void Remove(string home, string? configHome, string? machineName, string desktopId)
    {
        Edit(home, configHome, machineName, groups =>
        {
            var index = groups.FindIndex(entry => entry.DesktopIds.Contains(desktopId, StringComparer.Ordinal));
            if (index < 0)
            {
                return false;
            }
            groups.RemoveAt(index);
            return true;
        });
    }

    internal static void Add(string desktopId) => Add(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.MachineName, desktopId);

    internal static void Add(string home, string? configHome, string? machineName, string desktopId)
    {
        if (!IsValidDesktopId(desktopId))
        {
            throw new InvalidDataException("invalid desktop entry ID");
        }

        Edit(home, configHome, machineName, groups =>
        {
            var index = groups.FindIndex(entry => entry.DesktopIds.Contains(desktopId, StringComparer.Ordinal));
            if (index >= 0)
            {
                var alternatives = groups[index].DesktopIds;
                if (alternatives.Length == 1)
                {
                    return false;
                }
                // The chosen alternative is running beside the one already pinned.
                var remaining = alternatives.Where(id => id != desktopId).ToArray();
                groups[index] = (remaining, JsonSerializer.Serialize(remaining));
            }
            groups.Add(([desktopId], JsonSerializer.Serialize(desktopId)));
            return true;
        });
    }

    internal static void Move(string desktopId, string anchorId, bool after) => Move(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), Environment.MachineName, desktopId, anchorId, after);

    internal static void Move(string home, string? configHome, string? machineName, string desktopId, string anchorId, bool after)
    {
        Edit(home, configHome, machineName, groups =>
        {
            var from = groups.FindIndex(entry => entry.DesktopIds.Contains(desktopId, StringComparer.Ordinal));
            var to = groups.FindIndex(entry => entry.DesktopIds.Contains(anchorId, StringComparer.Ordinal));
            if (from < 0 || to < 0)
            {
                throw new InvalidDataException("the taskbar pin list changed");
            }
            if (from == to)
            {
                return false;
            }
            var moved = groups[from];
            groups.RemoveAt(from);
            groups.Insert(to - (from < to ? 1 : 0) + (after ? 1 : 0), moved);
            return true;
        });
    }

    private static void Edit(string home, string? configHome, string? machineName,
        Func<List<(string[] DesktopIds, string Json)>, bool> edit)
    {
        var (machineFile, sharedFile) = Paths(home, configHome, machineName);
        var source = SelectPath(machineFile, sharedFile);
        var groups = source is null
            ? PinnedApps.Table.Select(entry => (entry.DesktopIds, Json: JsonSerializer.Serialize(entry.DesktopIds))).ToList()
            : Read(source);
        if (!edit(groups))
        {
            return;
        }
        if (groups.Count > MaxGroups)
        {
            throw new InvalidDataException($"the taskbar supports at most {MaxGroups} pinned launchers");
        }
        var destination = machineFile ?? sharedFile;
        // Keep the surviving entries as written, including alternative IDs and
        // pins for apps that are currently unavailable on this machine.
        var entries = groups.Select(entry => entry.Json);
        var content = groups.Count == 0 ? "[]\n" : $"[\n  {string.Join(",\n  ", entries)}\n]\n";
        if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
        {
            content = $"[{string.Join(",", entries)}]";
            if (Encoding.UTF8.GetByteCount(content) > MaxBytes)
            {
                throw new InvalidDataException($"the launcher list exceeds {MaxBytes} bytes");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"pinned: cannot remove {temporary}: {exception.Message}");
            }
        }
    }

    private static List<(string[] DesktopIds, string Json)> Read(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var bytes = new MemoryStream();
        var buffer = new byte[4096];
        int count;
        while ((count = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (bytes.Length + count > MaxBytes)
            {
                throw new InvalidDataException($"file exceeds {MaxBytes} bytes");
            }
            bytes.Write(buffer, 0, count);
        }
        bytes.Position = 0;
        using var json = JsonDocument.Parse(bytes);
        if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > MaxGroups)
        {
            throw new InvalidDataException($"expected an array of at most {MaxGroups} launchers");
        }

        var groups = new List<(string[] DesktopIds, string Json)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in json.RootElement.EnumerateArray())
        {
            var ids = new List<string>();
            if (item.ValueKind == JsonValueKind.String)
            {
                AddId(item, ids, seen);
            }
            else if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() is > 0 and <= MaxAlternatives)
            {
                foreach (var alternative in item.EnumerateArray())
                {
                    AddId(alternative, ids, seen);
                }
            }
            else
            {
                throw new InvalidDataException($"each launcher must be an ID or 1–{MaxAlternatives} alternative IDs");
            }
            groups.Add(([.. ids], item.GetRawText()));
        }
        return groups;
    }

    private static void AddId(JsonElement value, List<string> ids, HashSet<string> seen)
    {
        var id = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (id is null || !IsValidDesktopId(id) || !seen.Add(id))
        {
            throw new InvalidDataException("desktop IDs must be unique non-empty .desktop basenames without path separators or control characters");
        }
        ids.Add(id);
    }

    internal static bool IsValidDesktopId(string id) =>
        id.Length is > 8 and <= MaxIdLength && id.EndsWith(".desktop", StringComparison.Ordinal) &&
        !id.Any(c => char.IsControl(c) || c is '/' or '\\');
}
