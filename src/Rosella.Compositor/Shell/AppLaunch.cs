using System.Diagnostics;

namespace Rosella.Compositor;

/// <summary>
/// The exec/env launcher helper: argv straight to exec (no shell), with
/// the spawn environment every launched app inherits — its Wayland socket
/// and dark mode, always (GTK3 reads GTK_THEME, GTK4/libadwaita reads the
/// debug colour-scheme override). There is no setting.
/// </summary>
internal static class AppLaunch
{
    /// <summary>The spawn environment for a launched app.</summary>
    internal static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> argv, string? socket = null, XWaylandSession? xWayland = null)
    {
        if (argv.Count == 0)
        {
            throw new ArgumentException("argv must not be empty", nameof(argv));
        }

        var startInfo = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false };
        if (socket is not null)
        {
            startInfo.Environment["WAYLAND_DISPLAY"] = socket;
        }

        // Toolkits should prefer the native Wayland backend even when an X11
        // display is available for compatibility.
        startInfo.Environment["XDG_SESSION_TYPE"] = "wayland";
        if (xWayland?.ApplyTo(startInfo) == true)
        {
            argv = ChromeProfile.PreferXWayland(argv as string[] ?? argv.ToArray());
        }
        for (var i = 1; i < argv.Count; i++)
        {
            startInfo.ArgumentList.Add(argv[i]);
        }

        var executable = Path.GetFileName(argv[0]);
        if (executable == "dbus-run-session")
        {
            executable = Path.GetFileName(argv.Skip(1).SkipWhile(argument => argument != "--").Skip(1).FirstOrDefault());
        }

        if (executable == "nautilus")
        {
            // The GTK theme override replaces libadwaita's stylesheet.
            // Nautilus needs that stylesheet to align its two header bars.
            startInfo.Environment.Remove("GTK_THEME");
        }
        else
        {
            startInfo.Environment["GTK_THEME"] = "Adwaita:dark";
        }
        startInfo.Environment["ADW_DEBUG_COLOR_SCHEME"] = "prefer-dark";
        return startInfo;
    }

    /// <summary>
    /// Spawns the argv and reports the new process's pid, or null when
    /// nothing started (a missing app simply does not open).
    /// </summary>
    internal static int? Spawn(IReadOnlyList<string> argv, string? socket = null, XWaylandSession? xWayland = null)
    {
        try
        {
            var startInfo = CreateStartInfo(argv, socket, xWayland);
            var process = Process.Start(startInfo);
            Console.WriteLine($"spawn: {startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)} pid {process?.Id.ToString() ?? "none"}");
            var pid = process?.Id;
            process?.Dispose();
            return pid;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"spawn: {string.Join(' ', argv)} failed: {exception.Message}");
            return null;
        }
    }
}
