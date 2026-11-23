using Rosella.Transport;

namespace Rosella.Compositor;

/// <summary>
/// Read-only, best-effort Chrome singleton detection at launch time.
/// </summary>
internal static class ChromeProfile
{
    internal enum ProcessSession { Missing, Same, Other, Unknown }
    internal enum LockKind { Missing, Symlink, Unknown }
    internal readonly record struct LockState(LockKind Kind, string? Target = null);
    internal sealed record State(
        string Home, string? ConfigHome, string? ChromeConfigHome, string? ChromeUserDataDir,
        string Hostname, Func<string, LockState> ReadLock, Func<int, ProcessSession> CheckProcess,
        Func<string, int, ProcessSession>? CheckFlatpakProcess = null);

    internal static string[] Prepare(string[] argv, string? display, bool secondary) =>
        Prepare(argv, LiveState(display), secondary);

    internal static string[] Prepare(string[] argv, State state, bool secondary = false)
    {
        // Respect explicit desktop-entry overrides and opaque wrappers rather than
        // guessing which profile or installation they actually use.
        if (HasExplicitProfile(argv))
        {
            return argv;
        }

        if (!TryCommand(argv, state, out var command, out state))
        {
            return argv;
        }
        var executable = Path.GetFileName(command[0]);
        var flatpak = IsFlatpakChrome(command);
        if (!flatpak && executable is not ("google-chrome" or "google-chrome-stable"))
        {
            return argv;
        }

        var config = AbsoluteOrNull(state.ConfigHome) ?? Path.Combine(state.Home, ".config");
        // Flatpak exports its app-specific config directory at this same home
        // path inside the sandbox; host XDG_CONFIG_HOME does not select it.
        var flatpakConfig = Path.Combine(state.Home, ".var", "app", "com.google.Chrome", "config");
        var profile = flatpak
            ? Path.Combine(flatpakConfig, "google-chrome")
            : AbsoluteOrNull(state.ChromeUserDataDir) ?? Path.Combine(AbsoluteOrNull(state.ChromeConfigHome) ?? config, "google-chrome");
        var fallback = Path.Combine(flatpak ? flatpakConfig : config, "rosella-user-data");
        if (flatpak)
        {
            // Closing the last dock-launched window should release the profile.
            argv = [.. argv, "--disable-background-mode"];
        }
        if (!(flatpak && secondary))
        {
            var owner = LockProcess(Path.Combine(profile, "SingletonLock"), state, flatpak);
            if (owner == ProcessSession.Missing || (!flatpak && owner == ProcessSession.Same))
            {
                return argv;
            }
            if (flatpak && owner == ProcessSession.Same)
            {
                Console.WriteLine($"pinned: Chrome launch waiting for the current browser to open a window or exit ({profile})");
                return [];
            }
        }

        // Chrome owns recovery for orphaned locks. A Flatpak invocation can't
        // safely hand off to a running browser that has no mapped window.
        var isolated = LockProcess(Path.Combine(fallback, "SingletonLock"), state, flatpak);
        if (isolated != ProcessSession.Missing)
        {
            Console.WriteLine($"pinned: Chrome launch waiting for profile {fallback} to be available ({isolated})");
            return [];
        }

        return Path.GetFullPath(profile) == Path.GetFullPath(fallback) ? argv : [.. argv, "--user-data-dir=" + fallback];
    }

    internal static bool HasExplicitProfile(string[] argv) =>
        argv.Any(arg => arg == "--user-data-dir" || arg.StartsWith("--user-data-dir=", StringComparison.Ordinal));

    internal static bool IsKnownChrome(string[] argv)
    {
        if (!TryCommand(argv, null, out var command, out _))
        {
            return false;
        }
        return Path.GetFileName(command[0]) is "google-chrome" or "google-chrome-stable" || IsFlatpakChrome(command);
    }

    // Handle the common desktop-entry form `env NAME=value ... command`.
    // Options and unknown syntax remain opaque, rather than guessing their effects.
    private static bool TryCommand(string[] argv, State? initial, out string[] command, out State state)
    {
        state = initial!;
        command = argv;
        if (argv.Length == 0)
        {
            return false;
        }
        if (Path.GetFileName(argv[0]) != "env")
        {
            return true;
        }
        var index = 1;
        while (index < argv.Length)
        {
            var equals = argv[index].IndexOf('=');
            if (equals <= 0)
            {
                break;
            }
            var name = argv[index][..equals];
            if (!(char.IsAsciiLetter(name[0]) || name[0] == '_') || name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            {
                return false;
            }
            if (initial is not null)
            {
                var value = argv[index][(equals + 1)..];
                state = name switch
                {
                    "HOME" => state with { Home = value },
                    "XDG_CONFIG_HOME" => state with { ConfigHome = value },
                    "CHROME_CONFIG_HOME" => state with { ChromeConfigHome = value },
                    "CHROME_USER_DATA_DIR" => state with { ChromeUserDataDir = value },
                    _ => state,
                };
            }
            index++;
        }
        if (index >= argv.Length || argv[index].StartsWith('-'))
        {
            return false;
        }
        command = argv[index..];
        return true;
    }

    internal static bool IsFlatpakChrome(string[] argv)
    {
        if (argv.Length < 3 || Path.GetFileName(argv[0]) != "flatpak" || argv[1] != "run")
        {
            return false;
        }

        // Accept the export's equals-form options and known flag options only.
        // An unknown bare option could consume the next word as its value, so
        // do not mistake that value for the app ID.
        foreach (var arg in argv.Skip(2))
        {
            if (!arg.StartsWith('-'))
            {
                return arg == "com.google.Chrome";
            }

            if (!arg.StartsWith("--", StringComparison.Ordinal) ||
                (!arg.Contains('=') && arg is not ("--file-forwarding" or "--user" or "--system")))
            {
                return false;
            }
        }

        return false;
    }

    private static string? AbsoluteOrNull(string? path) => !string.IsNullOrEmpty(path) && Path.IsPathFullyQualified(path) ? path : null;

    private static ProcessSession LockProcess(string lockPath, State environment, bool flatpak)
    {
        var state = environment.ReadLock(lockPath);
        if (state.Kind == LockKind.Missing)
        {
            return ProcessSession.Missing;
        }

        var target = state.Target;
        var separator = target?.LastIndexOf('-') ?? -1;
        if (state.Kind != LockKind.Symlink || separator <= 0 ||
            !int.TryParse(target![(separator + 1)..], out var pid) || pid <= 0 ||
            !string.Equals(target[..separator], environment.Hostname, StringComparison.Ordinal))
        {
            return ProcessSession.Unknown;
        }

        if (flatpak)
        {
            return environment.CheckFlatpakProcess?.Invoke(lockPath, pid) ?? ProcessSession.Unknown;
        }

        return environment.CheckProcess(pid);
    }

    private static State LiveState(string? display) => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        Environment.GetEnvironmentVariable("CHROME_CONFIG_HOME"),
        Environment.GetEnvironmentVariable("CHROME_USER_DATA_DIR"),
        Environment.MachineName, ReadLock, CheckProcess,
        (path, pid) => CheckFlatpakProcess(path, pid, Environment.GetEnvironmentVariable("XDG_SESSION_ID"), display));

    internal static ProcessSession CheckFlatpakProcess(string lockPath, int namespacePid, string? session, string? display,
        string procRoot = "/proc")
    {
        try
        {
            var profile = Path.GetDirectoryName(lockPath);
            var uid = StatusValues(File.ReadAllLines(Path.Combine(procRoot, "self", "status")), "Uid:").FirstOrDefault();
            if (uid is null)
            {
                return ProcessSession.Unknown;
            }

            foreach (var directory in Directory.EnumerateDirectories(procRoot))
            {
                if (!int.TryParse(Path.GetFileName(directory), out _))
                {
                    continue;
                }

                try
                {
                    if (File.ReadAllText(Path.Combine(directory, "comm")).TrimEnd() != "chrome")
                    {
                        continue;
                    }

                    var status = File.ReadAllLines(Path.Combine(directory, "status"));
                    var pids = StatusValues(status, "NSpid:");
                    if (StatusValues(status, "Uid:").FirstOrDefault() != uid)
                    {
                        continue;
                    }
                    if (pids.Length == 0 || !int.TryParse(pids[^1], out var pid))
                    {
                        return ProcessSession.Unknown;
                    }
                    if (pid != namespacePid)
                    {
                        continue;
                    }

                    var command = File.ReadAllText(Path.Combine(directory, "cmdline")).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    if (command.Length == 0 || command.Any(arg => arg.StartsWith("--type=", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var environment = File.ReadAllText(Path.Combine(directory, "environ")).Split('\0');
                    if (EnvironmentValue(environment, "FLATPAK_ID") != "com.google.Chrome")
                    {
                        continue;
                    }

                    var separator = Array.IndexOf(command, "--");
                    var options = separator < 0 ? command : command[..separator];
                    var explicitProfile = options.LastOrDefault(arg => arg.StartsWith("--user-data-dir=", StringComparison.Ordinal));
                    var config = EnvironmentValue(environment, "CHROME_CONFIG_HOME") ?? EnvironmentValue(environment, "XDG_CONFIG_HOME");
                    var dataDir = explicitProfile?["--user-data-dir=".Length..] ?? EnvironmentValue(environment, "CHROME_USER_DATA_DIR") ??
                        (config is null ? null : Path.Combine(config, "google-chrome"));
                    if (AbsoluteOrNull(dataDir) is null)
                    {
                        return ProcessSession.Unknown;
                    }
                    if (Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir!)) != profile)
                    {
                        continue;
                    }

                    return session is not null && session == EnvironmentValue(environment, "XDG_SESSION_ID") &&
                        display is not null && display == EnvironmentValue(environment, "WAYLAND_DISPLAY")
                        ? ProcessSession.Same : ProcessSession.Other;
                }
                catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
                {
                    // A procfs entry can disappear while the browser exits.
                }
            }

            // A live socket protects owners whose profile was specified by an alias.
            var socket = ReadLock(Path.Combine(profile!, "SingletonSocket"));
            if (socket.Kind == LockKind.Unknown || socket.Target is { } target &&
                File.ReadLines(Path.Combine(procRoot, "net", "unix")).Any(line => line.EndsWith(" " + target, StringComparison.Ordinal)))
            {
                return ProcessSession.Unknown;
            }

            // A very new lock can belong to a browser that started during the scan.
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) < TimeSpan.FromSeconds(2)
                ? ProcessSession.Unknown : ProcessSession.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return ProcessSession.Unknown;
        }
    }

    private static string[] StatusValues(string[] status, string field) =>
        status.FirstOrDefault(line => line.StartsWith(field, StringComparison.Ordinal))?[field.Length..]
            .Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries) ?? [];

    private static string? EnvironmentValue(string[] environment, string name) =>
        environment.FirstOrDefault(value => value.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..];

    internal static LockState ReadLock(string path)
    {
        try
        {
            _ = File.GetAttributes(path); // lstat semantics for dangling symlinks
            var info = new FileInfo(path);
            var target = info.LinkTarget; // Does not follow Chrome's deliberately dangling link.
            if (target is not null)
            {
                return new(LockKind.Symlink, target);
            }

            return new(LockKind.Unknown);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new(LockKind.Missing);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(LockKind.Unknown);
        }
    }

    private static ProcessSession CheckProcess(int pid)
    {
        var directory = $"/proc/{pid}";
        try
        {
            // A missing /proc itself is not evidence that the PID is dead.
            _ = File.GetAttributes("/proc");
            try
            {
                _ = File.GetAttributes(directory);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                return ProcessSession.Missing;
            }

            var currentSession = Environment.GetEnvironmentVariable("XDG_SESSION_ID");
            if (string.IsNullOrEmpty(currentSession))
            {
                return ProcessSession.Unknown;
            }

            // A recycled PID or a sandbox PID is not evidence of a Chrome
            // singleton in this session. Treat that ambiguity conservatively.
            var command = File.ReadAllText(Path.Combine(directory, "cmdline")).Split('\0')[0];
            var name = Path.GetFileName(command);
            if (name is not ("chrome" or "google-chrome" or "google-chrome-stable"))
            {
                return ProcessSession.Unknown;
            }

            var environ = File.ReadAllText(Path.Combine(directory, "environ"));
            var session = environ.Split('\0').FirstOrDefault(item => item.StartsWith("XDG_SESSION_ID=", StringComparison.Ordinal));
            return session is null ? ProcessSession.Unknown :
                session["XDG_SESSION_ID=".Length..] == currentSession ? ProcessSession.Same : ProcessSession.Other;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Missing process files after the directory check, permission errors
            // and inaccessible procfs are ambiguous.
            return ProcessSession.Unknown;
        }
    }
}
