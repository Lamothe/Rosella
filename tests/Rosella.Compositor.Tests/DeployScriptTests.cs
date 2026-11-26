using System.Diagnostics;
using System.Security.Cryptography;

namespace Rosella.Compositor.Tests;

/// <summary>Tests deployment registration checks with an isolated fake publish.</summary>
public sealed class DeployScriptTests : IDisposable
{
    private readonly string _root = Path.Combine("/tmp", $"rosella-deploy-{Guid.NewGuid():N}");
    private readonly string _project;
    private readonly string _home;
    private readonly string _installedSession;
    private readonly string _installedDesktop;
    private readonly string _dotnet;

    public DeployScriptTests()
    {
        _home = Path.Combine(_root, "home");
        _project = Path.Combine(_home, "Projects", "Rosella");
        var scripts = Path.Combine(_project, "scripts");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(_home);
        _installedSession = Path.Combine(_root, "installed-session");
        _installedDesktop = Path.Combine(_root, "installed-desktop");
        _dotnet = Path.Combine(_root, "dotnet");

        // Only the audited script may run on a development host. Any change
        // must be reviewed before redirecting its registration paths in tests.
        var deployBytes = File.ReadAllBytes(Path.Combine(RepoFiles.Root, "scripts", "deploy.sh"));
        var digest = Convert.ToHexString(SHA256.HashData(deployBytes));
        Assert.Equal("47A611FEE4DD6CBD2A8F22F73823962849603040AEB522B1899C5BE2B1E1626F", digest);
        var deploy = System.Text.Encoding.UTF8.GetString(deployBytes)
            .Replace("installed_session=/usr/local/bin/rosella-session", $"installed_session='{_installedSession}'", StringComparison.Ordinal)
            .Replace("installed_desktop=/usr/share/wayland-sessions/rosella.desktop", $"installed_desktop='{_installedDesktop}'", StringComparison.Ordinal);
        Assert.DoesNotContain("installed_session=/usr/local/bin/rosella-session", deploy);
        Assert.DoesNotContain("installed_desktop=/usr/share/wayland-sessions/rosella.desktop", deploy);
        File.WriteAllText(Path.Combine(scripts, "deploy.sh"), deploy);
        File.Copy(Path.Combine(RepoFiles.Root, "scripts", "rosella-session"), Path.Combine(scripts, "rosella-session"));
        File.Copy(Path.Combine(RepoFiles.Root, "scripts", "rosella.desktop"), Path.Combine(scripts, "rosella.desktop"));
        var taskbarBytes = File.ReadAllBytes(Path.Combine(RepoFiles.Root, "scripts", "publish-taskbar.sh"));
        Assert.Equal("14991C1C3F45B86552B221204B3F4FD34A382C09BACC907094A80E38807E91DA",
            Convert.ToHexString(SHA256.HashData(taskbarBytes)));
        WriteExecutable(Path.Combine(scripts, "publish-taskbar.sh"), System.Text.Encoding.UTF8.GetString(taskbarBytes));
        var dashboardBytes = File.ReadAllBytes(Path.Combine(RepoFiles.Root, "scripts", "publish-dashboard.sh"));
        Assert.Equal("26CA35F174BED750B978F6DF1990D081FC4BFD212127F14064C9D00FFB666912",
            Convert.ToHexString(SHA256.HashData(dashboardBytes)));
        WriteExecutable(Path.Combine(scripts, "publish-dashboard.sh"), System.Text.Encoding.UTF8.GetString(dashboardBytes));
        File.WriteAllText(Path.Combine(scripts, "rosella-dev.sh"), string.Empty);
        WriteExecutable(_dotnet, """
            #!/usr/bin/env bash
            if [ "$1" = build ]; then
                project=$(basename "$2" .csproj)
                while [ "$1" != -o ]; do shift; done
                printf 'module' > "$2/$project.dll"
                exit
            fi
            mkdir -p "$FAKE_PROJECT/build/publish"
            printf '' > "$FAKE_PROJECT/build/publish/libcoreclr.so"
            printf 'compositor' > "$FAKE_PROJECT/build/publish/Rosella.Compositor.dll"
            printf '#!/usr/bin/env bash\nexit 0\n' > "$FAKE_PROJECT/build/publish/Rosella.Compositor"
            chmod 755 "$FAKE_PROJECT/build/publish/Rosella.Compositor"
            """);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void UnregisteredVtInstall_PublishesWithoutWarning()
    {
        var result = Run();

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("WARNING:", result.Error);
        Assert.True(File.Exists(Path.Combine(_project, "build", "publish", "libcoreclr.so")));
        Assert.True(File.Exists(TaskbarGeneration.PublishedPath(Path.Combine(_project, "build", "publish"))));
        Assert.True(File.Exists(DashboardWidgetModules.PublishedPath(Path.Combine(_project, "build", "publish"))));
    }

    [Fact]
    public void DashboardOnly_ReplacesWidgetsInAnExistingPublish()
    {
        var publish = Directory.CreateDirectory(Path.Combine(_project, "build", "publish")).FullName;
        var host = Path.Combine(publish, "Rosella.Compositor.dll");
        File.WriteAllText(host, "existing publish");

        var result = Run(option: "--dashboard");

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(DashboardWidgetModules.PublishedPath(publish)));
        Assert.False(File.Exists(TaskbarGeneration.PublishedPath(publish)));
        Assert.Equal("existing publish", File.ReadAllText(host));
    }

    [Fact]
    public void MatchingRegistration_PublishesWithoutWarning()
    {
        InstallRegistration();

        var result = Run();

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("WARNING:", result.Error);
    }

    [Fact]
    public void DifferentCheckout_IsRejectedBeforePublishing()
    {
        var otherHome = Path.Combine(_root, "other-home");
        Directory.CreateDirectory(Path.Combine(otherHome, "Projects", "Rosella"));

        var result = Run(otherHome);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("both launchers use", result.Error);
        Assert.False(Directory.Exists(Path.Combine(_project, "build", "publish")));
    }

    [Theory]
    [InlineData("launcher")]
    [InlineData("desktop")]
    [InlineData("missing-launcher")]
    [InlineData("missing-desktop")]
    public void StaleOrPartialRegistration_WarnsWithoutBlockingPublish(string mismatch)
    {
        InstallRegistration();
        switch (mismatch)
        {
            case "launcher":
                File.AppendAllText(_installedSession, "# obsolete\n");
                break;
            case "desktop":
                File.AppendAllText(_installedDesktop, "# obsolete\n");
                break;
            case "missing-launcher":
                File.Delete(_installedSession);
                break;
            case "missing-desktop":
                File.Delete(_installedDesktop);
                break;
        }

        var result = Run();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("WARNING:", result.Error);
        Assert.Contains("login session may use an old build", result.Error);
        Assert.True(File.Exists(Path.Combine(_project, "build", "publish", "libcoreclr.so")));
    }

    private void InstallRegistration()
    {
        File.Copy(Path.Combine(_project, "scripts", "rosella-session"), _installedSession);
        File.Copy(Path.Combine(_project, "scripts", "rosella.desktop"), _installedDesktop);
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(_installedSession,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private (int ExitCode, string Error) Run(string? home = null, string? option = null)
    {
        var start = new ProcessStartInfo("bash")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.FileName = "timeout";
        start.ArgumentList.Add("--kill-after=1s");
        start.ArgumentList.Add("5s");
        start.ArgumentList.Add("bash");
        start.ArgumentList.Add(Path.Combine(_project, "scripts", "deploy.sh"));
        if (option is not null)
        {
            start.ArgumentList.Add(option);
        }
        start.Environment["HOME"] = home ?? _home;
        start.Environment["DOTNET"] = _dotnet;
        start.Environment["FAKE_PROJECT"] = _project;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(8000))
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("deploy.sh did not finish");
        }
        Assert.True(Task.WaitAll([output, error], TimeSpan.FromSeconds(1)), "deploy.sh kept a stream open");
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
