using System.Diagnostics;

namespace Rosella.Compositor;

/// <summary>Starts the user portals for a VT session when no desktop has started them.</summary>
internal sealed class PortalSession(string unit) : IDisposable
{
    private const string Desktop = "Rosella";
    private const string PortalUnit = "xdg-desktop-portal.service";

    internal static PortalSession? Start(string display)
    {
        var active = Run("systemctl", "--user", "is-active", "--quiet", "graphical-session.target").Ok;
        if (active && !Run("systemctl", "--user", "show-environment").Output.Split('\n')
            .Contains("XDG_SESSION_DESKTOP=rosella", StringComparer.Ordinal))
        {
            return null;
        }

        var installed = InstallConfiguration();

        // XDG Desktop Portal can combine Settings backends from each desktop
        // name. Only apps need the GNOME toolkit compatibility token.
        if (!Run("systemctl", "--user", "set-environment", $"WAYLAND_DISPLAY={display}",
            $"XDG_CURRENT_DESKTOP={Desktop}", "XDG_SESSION_DESKTOP=rosella", "XDG_SESSION_TYPE=wayland").Ok)
        {
            return null;
        }

        EnsureSecretService();

        // An existing portal keeps the desktop and preferences it started with.
        // Refresh it before accepting launch clicks, including on older login wrappers.
        if ((installed || PortalNeedsRefresh()) && !Run("systemctl", "--user", "try-restart", PortalUnit).Ok)
        {
            Console.Error.WriteLine("rosella: could not refresh the desktop portal for this session");
        }

        // The target refuses manual starts. Pull it in via a transient service tied to this PID.
        var unit = $"rosella-portals-{Environment.ProcessId}.service";
        if (Run("systemd-run", "--user", "--quiet", "--collect", $"--unit={unit}",
            "--service-type=exec", "--property=After=graphical-session.target",
            $"--property=Wants=graphical-session.target xdg-document-portal.service {PortalUnit}",
            "/usr/bin/tail", $"--pid={Environment.ProcessId}", "-f", "/dev/null").Ok)
        {
            Console.WriteLine("rosella: started the graphical user portal session");
            return new PortalSession(unit);
        }
        return null;
    }

    private static void EnsureSecretService()
    {
        var owner = Run("busctl", "--user", "--timeout=2s", "call", "org.freedesktop.DBus",
            "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s", "org.freedesktop.secrets");
        if (!owner.Ok || owner.Output.Trim() != "b false")
        {
            return;
        }

        const string unit = "oo7-daemon.service";
        var state = Run("systemctl", "--user", "show", "--property=LoadState", "--value", unit);
        if (state.Ok && state.Output.Trim() == "loaded" && !Run("systemctl", "--user", "start", unit).Ok)
        {
            Console.Error.WriteLine("rosella: the login keyring service could not be started");
        }
    }

    public void Dispose()
    {
        Run("systemctl", "--user", "stop", unit);
        if (!Run("systemctl", "--user", "is-active", "--quiet", "gnome-session.target").Ok)
        {
            Run("systemctl", "--user", "stop", "graphical-session.target");
        }
    }

    private static bool PortalNeedsRefresh()
    {
        var process = Run("systemctl", "--user", "show", "--property=MainPID", "--value", PortalUnit);
        if (!process.Ok || !int.TryParse(process.Output.Trim(), out var pid) || pid <= 0)
        {
            return false;
        }

        try
        {
            var environment = File.ReadAllText($"/proc/{pid}/environ");
            return !environment.Split('\0').Contains($"XDG_CURRENT_DESKTOP={Desktop}", StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The portal may have exited during the lookup. try-restart leaves an inactive unit alone.
            return true;
        }
    }

    private static bool InstallConfiguration()
    {
        const string name = "rosella-portals.conf";
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrEmpty(dataHome) || !Path.IsPathFullyQualified(dataHome))
        {
            dataHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        }

        var directory = Path.Combine(dataHome, "xdg-desktop-portal");
        var destination = Path.Combine(directory, name);
        if (File.Exists(destination))
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(directory);
            File.Copy(Path.Combine(AppContext.BaseDirectory, name), destination, overwrite: false);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            if (!File.Exists(destination))
            {
                Console.Error.WriteLine($"rosella: could not install portal preferences: {exception.Message}");
            }
            return false;
        }
    }

    private static (bool Ok, string Output) Run(string executable, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return (false, "");
            }
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(3000))
            {
                process.Kill();
                Console.Error.WriteLine($"rosella: {executable} timed out while starting portals");
                return (false, "");
            }
            return (process.ExitCode == 0, output.GetAwaiter().GetResult());
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"rosella: portal session unavailable ({error.Message})");
            return (false, "");
        }
    }
}
