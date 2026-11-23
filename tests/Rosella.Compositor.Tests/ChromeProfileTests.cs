using Rosella.Compositor;

namespace Rosella.Compositor.Tests;

public class ChromeProfileTests
{
    private static ChromeProfile.State State(ChromeProfile.LockState lockState, ChromeProfile.ProcessSession session = ChromeProfile.ProcessSession.Other) =>
        new("/home/test", "/config", null, null, "host-name", path => path.Contains("rosella-user-data", StringComparison.Ordinal) ? new(ChromeProfile.LockKind.Missing) : lockState, _ => session);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Native_DetectsOtherSessionAndStaleLock(int sessionValue, bool fallback)
    {
        var argv = new[] { "google-chrome", "--ozone-platform=wayland" };
        var result = ChromeProfile.Prepare(argv, State(new(ChromeProfile.LockKind.Symlink, "host-name-123"), (ChromeProfile.ProcessSession)sessionValue));
        Assert.Equal(fallback ? [.. argv, "--user-data-dir=/config/rosella-user-data"] : argv, result);
    }

    [Theory]
    [InlineData("remote-123")]
    [InlineData("broken")]
    [InlineData("host-name-0")]
    public void UnverifiableLocksFailSafe(string target)
    {
        var result = ChromeProfile.Prepare(["google-chrome"], State(new(ChromeProfile.LockKind.Symlink, target)));
        Assert.Equal("--user-data-dir=/config/rosella-user-data", result[^1]);
    }

    [Fact]
    public void MissingLockPreservesDefaultAndOverridesAreRespected()
    {
        var state = State(new(ChromeProfile.LockKind.Missing));
        Assert.Equal(["google-chrome"], ChromeProfile.Prepare(["google-chrome"], state));
        Assert.Equal(["google-chrome", "--user-data-dir=/custom"], ChromeProfile.Prepare(["google-chrome", "--user-data-dir=/custom"], State(new(ChromeProfile.LockKind.Unknown))));
        Assert.Equal(["wrapper"], ChromeProfile.Prepare(["wrapper"], State(new(ChromeProfile.LockKind.Unknown))));
    }

    [Theory]
    [InlineData("/home/michael/.local/bin/chrome-atomic-launch", "run", "com.google.Chrome")]
    [InlineData("/usr/bin/flatpak", "run", "org.example.Other", "com.google.Chrome")]
    public void UnrecognisedCommandsDoNotTriggerProfileDetection(params string[] argv)
    {
        var state = State(new(ChromeProfile.LockKind.Unknown)) with { ReadLock = _ => throw new InvalidOperationException("unexpected profile read") };
        Assert.Equal(argv, ChromeProfile.Prepare(argv, state));
    }

    [Fact]
    public void EnvWrappedChrome_UsesEffectiveConfigAndDetectsLockedProfile()
    {
        string? readPath = null;
        var state = State(new(ChromeProfile.LockKind.Unknown)) with
        {
            ReadLock = path =>
            {
                if (path.Contains("rosella-user-data", StringComparison.Ordinal)) { return new(ChromeProfile.LockKind.Missing); }
                readPath = path;
                return new(ChromeProfile.LockKind.Unknown);
            },
        };
        var argv = new[] { "env", "XDG_CONFIG_HOME=/different", "google-chrome" };
        Assert.True(ChromeProfile.IsKnownChrome(argv));
        var result = ChromeProfile.Prepare(argv, state);
        Assert.Equal("/different/google-chrome/SingletonLock", readPath);
        Assert.Equal("--user-data-dir=/different/rosella-user-data", result[^1]);
        Assert.False(ChromeProfile.IsKnownChrome(["env", "--unset=HOME", "google-chrome"]));
    }

    [Fact]
    public void FlatpakUsesAppHomeForBothLockAndFallback()
    {
        string? readPath = null;
        var state = State(new(ChromeProfile.LockKind.Missing)) with
        {
            ReadLock = path => { if (path.Contains("rosella-user-data", StringComparison.Ordinal)) { return new(ChromeProfile.LockKind.Missing); } readPath = path; return new(ChromeProfile.LockKind.Symlink, "host-name-123"); },
            CheckProcess = _ => ChromeProfile.ProcessSession.Missing,
        };
        var result = ChromeProfile.Prepare(["/usr/bin/flatpak", "run", "--file-forwarding", "com.google.Chrome", "@@", "--ozone-platform=wayland"], state);
        Assert.Equal("/home/test/.var/app/com.google.Chrome/config/google-chrome/SingletonLock", readPath);
        Assert.Equal("--user-data-dir=/home/test/.var/app/com.google.Chrome/config/rosella-user-data", result[^1]);
    }

    [Fact]
    public void FlatpakKeepsUsualProfileWhenItsLockHasNoOwner()
    {
        var argv = new[] { "flatpak", "run", "com.google.Chrome" };
        var state = State(new(ChromeProfile.LockKind.Symlink, "host-name-2")) with
        {
            CheckFlatpakProcess = (_, pid) => pid == 2 ? ChromeProfile.ProcessSession.Missing : ChromeProfile.ProcessSession.Unknown,
            CheckProcess = _ => throw new InvalidOperationException("sandbox PID is not a host PID"),
        };
        Assert.Equal([.. argv, "--disable-background-mode"], ChromeProfile.Prepare(argv, state));
    }

    [Theory]
    [InlineData(0, true)]  // Stale lock
    [InlineData(1, false)] // Live on this desktop; mapped windows are focused before launch
    [InlineData(2, false)] // A different desktop or login
    [InlineData(3, false)] // Unverifiable owner
    public void SecondaryFlatpakUsesOnlyItsOwnProfile(int owner, bool launches)
    {
        string? checkedLock = null;
        var argv = new[] { "flatpak", "run", "com.google.Chrome" };
        var state = State(new(ChromeProfile.LockKind.Unknown)) with
        {
            ReadLock = _ => new(ChromeProfile.LockKind.Symlink, "host-name-2"),
            CheckFlatpakProcess = (path, _) => { checkedLock = path; return (ChromeProfile.ProcessSession)owner; },
        };

        Assert.Equal(launches ? [.. argv, "--disable-background-mode", "--user-data-dir=/home/test/.var/app/com.google.Chrome/config/rosella-user-data"] : [],
            ChromeProfile.Prepare(argv, state, secondary: true));
        Assert.Equal("/home/test/.var/app/com.google.Chrome/config/rosella-user-data/SingletonLock", checkedLock);
    }

    [Fact]
    public void FlatpakFindsTheOwnerOfTheRequestedProfileWhenSandboxPidsCollide()
    {
        var root = Directory.CreateTempSubdirectory("rosella-chrome-proc-").FullName;
        try
        {
            var proc = Directory.CreateDirectory(Path.Combine(root, "proc")).FullName;
            Directory.CreateDirectory(Path.Combine(proc, "self"));
            File.WriteAllText(Path.Combine(proc, "self", "status"), "Uid:\t1000\t1000\t1000\t1000\n");
            var profile = Directory.CreateDirectory(Path.Combine(root, "rosella-user-data")).FullName;
            var lockPath = Path.Combine(profile, "SingletonLock");
            File.WriteAllText(lockPath, "");
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow.AddMinutes(-2));

            void WriteBrowser(int hostPid, string display, string profilePath)
            {
                var directory = Directory.CreateDirectory(Path.Combine(proc, hostPid.ToString())).FullName;
                File.WriteAllText(Path.Combine(directory, "comm"), "chrome\n");
                File.WriteAllText(Path.Combine(directory, "status"), $"Uid:\t1000\t1000\t1000\t1000\nNSpid:\t{hostPid}\t2\n");
                File.WriteAllText(Path.Combine(directory, "cmdline"), $"/app/extra/chrome\0--user-data-dir={profilePath}\0");
                File.WriteAllText(Path.Combine(directory, "environ"), $"FLATPAK_ID=com.google.Chrome\0XDG_SESSION_ID=11\0WAYLAND_DISPLAY={display}\0");
            }

            WriteBrowser(100, "wayland-0", Path.Combine(root, "google-chrome"));
            WriteBrowser(200, "wayland-0-dell", profile);
            Assert.Equal(ChromeProfile.ProcessSession.Same, ChromeProfile.CheckFlatpakProcess(lockPath, 2, "11", "wayland-0-dell", proc));
            Assert.Equal(ChromeProfile.ProcessSession.Other, ChromeProfile.CheckFlatpakProcess(lockPath, 2, "11", "wayland-0", proc));
            Assert.Equal(ChromeProfile.ProcessSession.Other, ChromeProfile.CheckFlatpakProcess(lockPath, 2, "12", "wayland-0-dell", proc));

            Directory.Delete(Path.Combine(proc, "200"), recursive: true);
            Assert.Equal(ChromeProfile.ProcessSession.Missing, ChromeProfile.CheckFlatpakProcess(lockPath, 2, "11", "wayland-0-dell", proc));
            File.SetLastWriteTimeUtc(lockPath, DateTime.UtcNow);
            Assert.Equal(ChromeProfile.ProcessSession.Unknown, ChromeProfile.CheckFlatpakProcess(lockPath, 2, "11", "wayland-0-dell", proc));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeHonoursChromeEnvironmentPrecedence()
    {
        string? readPath = null;
        var state = State(new(ChromeProfile.LockKind.Missing)) with
        {
            ChromeConfigHome = "/chrome-config", ChromeUserDataDir = "/chrome-data",
            ReadLock = path => { readPath = path; return new(ChromeProfile.LockKind.Missing); },
        };
        ChromeProfile.Prepare(["google-chrome"], state);
        Assert.Equal("/chrome-data/SingletonLock", readPath);
        ChromeProfile.Prepare(["google-chrome"], state with { ChromeUserDataDir = null });
        Assert.Equal("/chrome-config/google-chrome/SingletonLock", readPath);
    }

    [Fact]
    public void RealDanglingSymlinkIsReadWithoutFollowingOrChangingIt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-chrome-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "SingletonLock");
            Assert.Equal(ChromeProfile.LockKind.Missing, ChromeProfile.ReadLock(path).Kind);
            File.CreateSymbolicLink(path, "host-name-123");
            Assert.Equal(new(ChromeProfile.LockKind.Symlink, "host-name-123"), ChromeProfile.ReadLock(path));
            Assert.Equal("host-name-123", new FileInfo(path).LinkTarget);
            Assert.True(new FileInfo(path).LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-1));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void NativeFallbackOnlyReusesVerifiablyDeadLock(int session, bool reusable)
    {
        var state = State(new(ChromeProfile.LockKind.Unknown)) with
        {
            ReadLock = path => path.Contains("rosella-user-data", StringComparison.Ordinal)
                ? new(ChromeProfile.LockKind.Symlink, "host-name-123") : new(ChromeProfile.LockKind.Unknown),
            CheckProcess = _ => (ChromeProfile.ProcessSession)session,
        };
        var result = ChromeProfile.Prepare(["google-chrome"], state);
        Assert.Equal(reusable ? ["google-chrome", "--user-data-dir=/config/rosella-user-data"] : [], result);
    }

    [Theory]
    [InlineData(2, null)]
    [InlineData(1, "remote-123")]
    [InlineData(1, "malformed")]
    public void AmbiguousNativeFallbackBlocksLaunch(int kind, string? target)
    {
        var state = State(new(ChromeProfile.LockKind.Unknown)) with { ReadLock = _ => new((ChromeProfile.LockKind)kind, target) };
        Assert.Empty(ChromeProfile.Prepare(["google-chrome"], state));
    }

    [Fact]
    public void FlatpakFallbackNeverTreatsSandboxPidAsHostPid()
    {
        var state = State(new(ChromeProfile.LockKind.Symlink, "host-name-2")) with
        {
            ReadLock = _ => new(ChromeProfile.LockKind.Symlink, "host-name-2"),
            CheckProcess = _ => throw new InvalidOperationException("sandbox PID is not a host PID"),
        };
        Assert.Empty(ChromeProfile.Prepare(["flatpak", "run", "com.google.Chrome"], state));
    }

    [Fact]
    public void BlockedLaunchDoesNotSpawn()
    {
        var launcher = new PinnedLauncher("Chrome", "google-chrome.desktop", [], ["google-chrome"], null, null, _ => []);
        var windows = new TestWindows();
        launcher.Click(windows);
        Assert.Equal(0, windows.Launches);
    }

    [Fact]
    public void ClickChecksTheProfileAgainEveryLaunch()
    {
        var calls = 0;
        var launcher = new PinnedLauncher("Chrome", "google-chrome.desktop", [], ["google-chrome"], null, null,
            argv => { calls++; return argv; });
        var windows = new TestWindows();
        launcher.Click(windows);
        launcher.Click(windows);
        Assert.Equal(2, calls);
    }

    private sealed class TestWindows : IWindowRegistry
    {
        public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys) => null;
        public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys) { }
        public int Launches { get; private set; }
        public int? Launch(IReadOnlyList<string> argv) { Launches++; return null; }
    }
}
