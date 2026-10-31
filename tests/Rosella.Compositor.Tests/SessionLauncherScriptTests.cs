using System.Diagnostics;
using System.Security.Cryptography;

namespace Rosella.Compositor.Tests;

/// <summary>Checks the GDM launcher against stub executables, never a compositor.</summary>
public sealed class SessionLauncherScriptTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", $"rosella-session-{Guid.NewGuid():N}");
    private readonly string _home;
    private readonly string _bin;
    private readonly string _capture;
    private readonly string _fallback;
    private readonly string _launcher;

    public SessionLauncherScriptTests()
    {
        _home = Path.Combine(_root, "home");
        _bin = Path.Combine(_root, "bin");
        _capture = Path.Combine(_root, "capture");
        _fallback = Path.Combine(_root, "fallback-compositor");
        _launcher = Path.Combine(_root, "rosella-session");
        Directory.CreateDirectory(_home);
        Directory.CreateDirectory(_bin);

        // Even a regression that executes the fallback directly can only run
        // this stub, never the host's installed compositor.
        var scriptBytes = File.ReadAllBytes(Path.Combine(RepoFiles.Root, "scripts", "rosella-session"));
        var script = System.Text.Encoding.UTF8.GetString(scriptBytes);
        var digest = Convert.ToHexString(SHA256.HashData(scriptBytes));
        // Fail closed if launcher code changes: the test must be re-reviewed
        // before any modified shell script can execute on a development host.
        Assert.Equal("B90528E0DABA549C284457B06AF57C93A62D124C446F7B99B5D42438F4C00222", digest);
        const string fallbackPath = "/usr/local/lib/rosella/Rosella.Compositor";
        Assert.Contains(fallbackPath, script);
        script = script.Replace(fallbackPath, _fallback, StringComparison.Ordinal);
        Assert.DoesNotContain(fallbackPath, script);
        File.WriteAllText(_launcher, script);
        WriteExecutable(_fallback, """
            #!/usr/bin/env bash
            printf 'fallback\n%s\n%s\n%s\n' "$0" "$XDG_CURRENT_DESKTOP" "$XDG_SESSION_TYPE" > "$CAPTURE"
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ValidUserBuild_IsSelectedForBothInputGroupPaths(bool hasInputGroup)
    {
        var userBuild = Path.Combine(_home, "Projects", "Rosella", "build", "publish");
        Directory.CreateDirectory(userBuild);
        File.WriteAllText(Path.Combine(userBuild, "libcoreclr.so"), string.Empty);
        WriteExecutable(Path.Combine(userBuild, "Rosella.Compositor"), """
            #!/usr/bin/env bash
            printf 'user\n%s\n%s\n' "$XDG_CURRENT_DESKTOP" "$XDG_SESSION_TYPE" > "$CAPTURE"
            for arg in "$@"; do printf '%s\n' "$arg" >> "$CAPTURE"; done
            """);

        var output = Run(hasInputGroup, "argument with spaces");

        Assert.Equal(0, output.ExitCode);
        var expected = hasInputGroup
            ? new[] { "user", "Rosella", "wayland", "argument with spaces" }
            : new[] { "user", "Rosella", "wayland" }; // GDM passes no arguments via sg.
        Assert.Equal(expected, File.ReadAllLines(_capture));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    public void MissingOrIncompleteUserBuild_FallsBackWithoutRunningSystemCompositor(bool hasBinary, bool hasRuntime, bool hasInputGroup)
    {
        var userBuild = Path.Combine(_home, "Projects", "Rosella", "build", "publish");
        Directory.CreateDirectory(userBuild);
        if (hasBinary)
        {
            WriteExecutable(Path.Combine(userBuild, "Rosella.Compositor"), "#!/usr/bin/env bash\nexit 1\n");
        }
        if (hasRuntime)
        {
            File.WriteAllText(Path.Combine(userBuild, "libcoreclr.so"), string.Empty);
        }

        var output = Run(hasInputGroup);

        Assert.Equal(0, output.ExitCode);
        Assert.Equal(new[] { "fallback", _fallback, "Rosella", "wayland" }, File.ReadAllLines(_capture));
    }

    private (int ExitCode, string Error) Run(bool hasInputGroup, params string[] arguments)
    {
        WriteExecutable(Path.Combine(_bin, "id"), hasInputGroup
            ? "#!/usr/bin/env bash\nprintf 'michael input\\n'\n"
            : "#!/usr/bin/env bash\nprintf 'michael\\n'\n");
        WriteExecutable(Path.Combine(_bin, "sg"), """
            #!/usr/bin/env bash
            if [ "$1" != input ] || [ "$2" != -c ]; then exit 2; fi
            exec /bin/sh -c "$3"
            """);

        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        // GNU timeout bounds descendants that keep stderr open after the
        // launcher exits. The script digest above limits what may be run.
        start.FileName = "timeout";
        start.ArgumentList.Add("--kill-after=1s");
        start.ArgumentList.Add("5s");
        start.ArgumentList.Add("bash");
        start.ArgumentList.Add(_launcher);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["HOME"] = _home;
        start.Environment["CAPTURE"] = _capture;
        start.Environment["PATH"] = $"{_bin}:{Environment.GetEnvironmentVariable("PATH")}";

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(8000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("rosella-session did not finish");
        }
        Assert.True(error.Wait(TimeSpan.FromSeconds(1)), "rosella-session kept stderr open");
        return (process.ExitCode, error.Result);
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content + "\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
