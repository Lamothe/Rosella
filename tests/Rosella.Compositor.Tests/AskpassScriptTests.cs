using System.Diagnostics;
using System.Net.Sockets;

namespace Rosella.Compositor.Tests;

/// <summary>
/// scripts/rosella-askpass end to end against stub driver and zenity: the
/// dialog must aim at the active session's display, and an active session
/// without a display name must never blank the ambient fallback (a bug
/// that once sent the sudo prompt to a display nobody was watching).
/// </summary>
public sealed class AskpassScriptTests : IDisposable
{
    private readonly string _root;
    private readonly string _capture;
    private readonly Socket _socket;

    public AskpassScriptTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rosella-askpass-{Guid.NewGuid():N}");
        var bin = Path.Combine(_root, "bin");
        var run = Path.Combine(_root, "run");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(run);
        _capture = Path.Combine(_root, "zenity-display.txt");

        // The stub driver prints a canned state file; the stub zenity
        // records which WAYLAND_DISPLAY it was launched with.
        WriteExecutable(Path.Combine(bin, "rosella-driver"), $"#!/usr/bin/env bash\ncat '{_root}/state.txt' 2>/dev/null || true\n");
        WriteExecutable(Path.Combine(bin, "zenity"), $"#!/usr/bin/env bash\nprintf '%s' \"${{WAYLAND_DISPLAY-}}\" > '{_capture}'\nexit 0\n");

        // A real socket file: the script skips anything that is not one.
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Bind(new UnixDomainSocketEndPoint(Path.Combine(run, "rosella-control-test.sock")));
    }

    public void Dispose()
    {
        _socket.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void ActiveSessionWithADisplay_AimsTheDialogAtIt()
    {
        Assert.Equal("wayland-1", Run("vt 3 active\ndisplay wayland-1", ambient: "wayland-0"));
    }

    [Fact]
    public void InactiveSession_FallsBackToTheAmbientDisplay()
    {
        Assert.Equal("wayland-0", Run("vt 3 inactive\ndisplay wayland-9", ambient: "wayland-0"));
    }

    [Fact]
    public void ActiveSessionWithoutADisplay_KeepsTheAmbientFallback()
    {
        // The fix: a display-less session must not blank the fallback.
        Assert.Equal("wayland-0", Run("vt 3 active", ambient: "wayland-0"));
    }

    [Fact]
    public void NoStateAtAll_FallsBackToTheAmbientDisplay()
    {
        Assert.Equal("wayland-0", Run(string.Empty, ambient: "wayland-0"));
    }

    /// <summary>Runs the real script and returns the display the stub zenity saw.</summary>
    private string Run(string state, string ambient)
    {
        File.WriteAllText(Path.Combine(_root, "state.txt"), state);
        File.Delete(_capture);

        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
        };
        start.ArgumentList.Add(RepoFiles.AskpassScript);
        start.Environment["XDG_RUNTIME_DIR"] = Path.Combine(_root, "run");
        start.Environment["WAYLAND_DISPLAY"] = ambient;
        start.Environment["HOME"] = _root; // no real driver under this home
        start.Environment["PATH"] = $"{Path.Combine(_root, "bin")}:{Environment.GetEnvironmentVariable("PATH")}";

        using var process = Process.Start(start)!;
        Assert.True(process.WaitForExit(5000), "rosella-askpass did not finish");
        Assert.Equal(0, process.ExitCode);
        return File.Exists(_capture) ? File.ReadAllText(_capture) : "<no dialog>";
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
