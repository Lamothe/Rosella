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
    internal static ProcessStartInfo CreateStartInfo(IReadOnlyList<string> argv, string? socket = null)
    {
        if (argv.Count == 0)
        {
            throw new ArgumentException("argv must not be empty", nameof(argv));
        }

        var startInfo = new ProcessStartInfo { FileName = argv[0], UseShellExecute = false };
        for (var i = 1; i < argv.Count; i++)
        {
            startInfo.ArgumentList.Add(argv[i]);
        }

        if (socket is not null)
        {
            startInfo.Environment["WAYLAND_DISPLAY"] = socket;
        }

        // The session type is how every toolkit chooses between Wayland and
        // X11, and Rosella has no X server. Launched from a text console the
        // variable is unset or "tty", which sent Chromium-family apps looking
        // for an X display that does not exist — they died at startup. Saying
        // "wayland" is the truth and is all they need.
        startInfo.Environment["XDG_SESSION_TYPE"] = "wayland";

        startInfo.Environment["GTK_THEME"] = "Adwaita:dark";
        startInfo.Environment["ADW_DEBUG_COLOR_SCHEME"] = "prefer-dark";
        return startInfo;
    }

    /// <summary>Spawns the argv. A missing app simply does not open.</summary>
    internal static void Spawn(IReadOnlyList<string> argv, string? socket = null)
    {
        try
        {
            var process = Process.Start(CreateStartInfo(argv, socket));
            Console.WriteLine($"spawn: {string.Join(' ', argv)} pid {process?.Id.ToString() ?? "none"}");
            process?.Dispose();
        }
        catch (Exception exception)
        {
            Console.WriteLine($"spawn: {string.Join(' ', argv)} failed: {exception.Message}");
        }
    }
}
