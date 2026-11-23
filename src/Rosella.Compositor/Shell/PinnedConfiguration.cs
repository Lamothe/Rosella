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
        var root = !string.IsNullOrEmpty(configHome) && Path.IsPathFullyQualified(configHome)
            ? configHome : Path.Combine(home, ".config");
        var directory = Path.Combine(root, "rosella");
        if (!string.IsNullOrEmpty(machineName) && machineName.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            var machineFile = Path.Combine(directory, $"launchers.{machineName}.json");
            if (File.Exists(machineFile))
            {
                return machineFile;
            }
        }

        var sharedFile = Path.Combine(directory, "launchers.json");
        return File.Exists(sharedFile) ? sharedFile : null;
    }

    internal static string[][]? Load(string? path)
    {
        if (path is null)
        {
            return null;
        }

        try
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

            var groups = new List<string[]>();
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
                groups.Add([.. ids]);
            }
            return [.. groups];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException)
        {
            Console.Error.WriteLine($"pinned: cannot load {path}: {exception.Message}; using defaults");
            return null;
        }
    }

    private static void AddId(JsonElement value, List<string> ids, HashSet<string> seen)
    {
        var id = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || id.Length > MaxIdLength || !id.EndsWith(".desktop", StringComparison.Ordinal)
            || id == ".desktop" || id.Any(c => char.IsControl(c) || c is '/' or '\\') || !seen.Add(id))
        {
            throw new InvalidDataException("desktop IDs must be unique non-empty .desktop basenames without path separators or control characters");
        }
        ids.Add(id);
    }
}
