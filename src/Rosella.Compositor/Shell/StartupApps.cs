namespace Rosella.Compositor;

/// <summary>The dock apps selected to start on this machine at Rosella login.</summary>
internal sealed class StartupApps(string path)
{
    private readonly object _gate = new();

    internal static StartupApps ForMachine()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(config) || !Path.IsPathFullyQualified(config))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return new StartupApps(Path.Combine(config, "rosella", $"startup-apps.{Environment.MachineName}"));
    }

    internal bool Contains(string desktopId)
    {
        lock (_gate)
        {
            return Read().Contains(desktopId);
        }
    }

    internal void Toggle(string desktopId)
    {
        lock (_gate)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                var ids = Read();
                if (!ids.Remove(desktopId))
                {
                    ids.Add(desktopId);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllLines(temporary, ids.Order(StringComparer.Ordinal));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"startup apps: could not save {path}: {error.Message}");
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    try { File.Delete(temporary); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        Console.Error.WriteLine($"startup apps: could not remove {temporary}: {error.Message}");
                    }
                }
            }
        }
    }

    internal void Launch(IReadOnlyList<PinnedLauncher> pins, IWindowRegistry windows)
    {
        HashSet<string> ids;
        lock (_gate)
        {
            ids = Read();
        }

        foreach (var pin in pins)
        {
            if (ids.Remove(pin.DesktopId))
            {
                pin.Click(windows);
            }
        }
    }

    private HashSet<string> Read()
    {
        try
        {
            return File.ReadLines(path).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"startup apps: could not read {path}: {error.Message}");
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }
}
