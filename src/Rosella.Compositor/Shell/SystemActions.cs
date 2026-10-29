using System.Diagnostics;

namespace Rosella.Compositor;

/// <summary>What the apple menu's three items do. Tests fake it.</summary>
internal interface ISystemActions
{
    /// <summary>Ends the session the graceful way (the Super+Esc quit path).</summary>
    void LogOut();

    /// <summary>Reboots the machine.</summary>
    void Restart();

    /// <summary>Powers the machine off.</summary>
    void ShutDown();
}

/// <summary>
/// The real system actions: log out is the compositor's graceful quit;
/// restart and shut down are fire-and-forget systemctl spawns on the
/// thread pool — the render and input threads never block on them, and a
/// spawn failure is only logged.
/// </summary>
internal sealed class SystemActions(Action logOut) : ISystemActions
{
    public void LogOut() => logOut();

    public void Restart() => SpawnSystemctl("reboot");

    public void ShutDown() => SpawnSystemctl("poweroff");

    private static void SpawnSystemctl(string verb)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var startInfo = new ProcessStartInfo { FileName = "systemctl", UseShellExecute = false };
                startInfo.ArgumentList.Add(verb);
                var process = Process.Start(startInfo);
                Console.WriteLine($"system: systemctl {verb} pid {process?.Id.ToString() ?? "none"}");
                process?.Dispose();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"system: systemctl {verb} failed: {exception.Message}");
            }
        });
    }
}
