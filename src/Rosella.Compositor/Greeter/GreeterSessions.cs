namespace Rosella.Compositor.Greeter;

/// <summary>One wayland-sessions desktop entry, ready to start through greetd.</summary>
internal sealed record GreeterSession(string Id, string Name, string[] Command, string[] Environment);

/// <summary>
/// The greeter's session list, read from the same directory GDM uses:
/// /usr/share/wayland-sessions/*.desktop. Names are the plain Name= value
/// (DesktopEntry deliberately ignores localised keys) and the command is the
/// Exec= value parsed GLib-style, so a quoted path or an argument stays one
/// argv word.
/// </summary>
internal static class GreeterSessions
{
    /// <summary>The XDG wayland session directory (greetd reads the same files).</summary>
    internal const string DefaultDirectory = "/usr/share/wayland-sessions";

    /// <summary>The session this machine is meant to boot into, when offered.</summary>
    internal const string PreferredId = "rosella.desktop";

    /// <summary>
    /// Every usable session, sorted by file name for a stable picker.
    /// Hidden entries, entries without Exec, unparsable Exec values and
    /// unreadable files simply do not appear.
    /// </summary>
    internal static IReadOnlyList<GreeterSession> Load(string directory)
    {
        var sessions = new List<GreeterSession>();
        try
        {
            if (!Directory.Exists(directory))
            {
                return sessions;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*.desktop").OrderBy(path => path, StringComparer.Ordinal))
            {
                var entry = DesktopEntry.ParseFile(path);
                if (entry is null || entry.IsHidden || string.IsNullOrWhiteSpace(entry.Exec))
                {
                    continue;
                }

                var command = DesktopEntry.ParseExec(entry.Exec);
                if (command is null)
                {
                    continue;
                }

                var id = Path.GetFileName(path);
                var name = string.IsNullOrWhiteSpace(entry.Name)
                    ? Path.GetFileNameWithoutExtension(path)
                    : entry.Name;

                // greetd sets none of these, and logind needs
                // XDG_SESSION_TYPE before the PAM session opens (a session
                // wrapper exporting it after the fact is too late for the
                // session type). DesktopNames is semicolon-separated in the
                // file and colon-separated in XDG_CURRENT_DESKTOP.
                var desktopId = Path.GetFileNameWithoutExtension(id);
                var desktopNames = entry.DesktopNames
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var currentDesktop = desktopNames.Length > 0
                    ? string.Join(':', desktopNames)
                    : desktopId;

                sessions.Add(new GreeterSession(
                    id,
                    name,
                    command,
                    [
                        "XDG_SESSION_TYPE=wayland",
                        $"XDG_SESSION_DESKTOP={desktopId}",
                        $"XDG_CURRENT_DESKTOP={currentDesktop}",
                    ]));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rosella: greeter: cannot read {directory}: {exception.Message}");
        }

        return sessions;
    }

    /// <summary>The initial picker selection: Rosella when present, else the first row.</summary>
    internal static int PreferredIndex(IReadOnlyList<GreeterSession> sessions)
    {
        for (var index = 0; index < sessions.Count; index++)
        {
            if (string.Equals(sessions[index].Id, PreferredId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return 0;
    }
}