using Rosella.Compositor;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The pinned launchers: the static table, app-id matching (running
/// indicator and window claims), and the focus-or-launch click routing
/// over a fake window registry.
/// </summary>
public class PinnedAppsTests
{
    [Fact]
    public void Table_ListsTheDefaultLaunchersInOrder()
    {
        Assert.Equal(
            ["Google Chrome", "Firefox", "Terminal", "Files", "Visual Studio Code", "Resources", "ChatGPT", "Blender", "Commander"],
            PinnedApps.Table.Select(entry => entry.Name));
    }

    [Fact]
    public void Table_CandidatesMatchTheSpec()
    {
        Assert.Equal(
            ["google-chrome.desktop", "google-chrome-stable.desktop", "com.google.Chrome.desktop"],
            PinnedApps.Table[0].DesktopIds);
        Assert.Equal(["org.mozilla.firefox.desktop", "firefox.desktop"], PinnedApps.Table[1].DesktopIds);
        Assert.Equal(["--ozone-platform=wayland"], PinnedApps.Table[0].Arguments);
        Assert.Equal(
            ["org.gnome.Ptyxis.desktop", "org.gnome.Console.desktop", "gnome-terminal.desktop", "org.gnome.Terminal.desktop"],
            PinnedApps.Table[2].DesktopIds);
        Assert.Equal(["org.gnome.Nautilus.desktop", "nautilus.desktop"], PinnedApps.Table[3].DesktopIds);
        Assert.Equal(["com.visualstudio.code.desktop", "com.microsoft.VSCode.desktop", "code.desktop", "code-oss.desktop"], PinnedApps.Table[4].DesktopIds);
        Assert.Equal(["net.nokyan.Resources.desktop", "org.gnome.World.Resources.desktop", "resources.desktop"], PinnedApps.Table[5].DesktopIds);
    }

    [Fact]
    public void Resolve_UsesInstalledFlatpakForOpaqueUserChromeWrapperOnlyForExecution()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-pins-{Guid.NewGuid():N}");
        var user = Path.Combine(root, "user", "applications");
        var system = Path.Combine(root, "system", "applications");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(system);
        var iconDirectory = Path.Combine(root, "icons", "hicolor", "48x48", "apps");
        Directory.CreateDirectory(iconDirectory);
        try
        {
            var iconPath = Path.Combine(iconDirectory, "com.google.Chrome.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "icon.png"), iconPath);
            File.WriteAllText(Path.Combine(user, "com.google.Chrome.desktop"), """
                [Desktop Entry]
                Name=My Chrome
                Exec=/home/michael/.local/bin/chrome-atomic-launch @@u %U @@
                StartupWMClass=google-chrome
                Icon=com.google.Chrome
                """);
            File.WriteAllText(Path.Combine(system, "com.google.Chrome.desktop"), """
                [Desktop Entry]
                Name=Google Chrome
                Exec=/usr/bin/flatpak run --branch=stable --arch=x86_64 --command=/app/bin/chrome --file-forwarding com.google.Chrome @@u %U @@
                """);
            var launcher = Assert.Single(PinnedApps.Resolve(24, [user, system], [root]));
            Assert.Equal("My Chrome", launcher.Name);
            Assert.Equal("com.google.Chrome.desktop", launcher.DesktopId);
            Assert.Equal(iconPath, launcher.IconPath);
            Assert.NotNull(launcher.Icon);
            Assert.Equal(["/usr/bin/flatpak", "run", "--branch=stable", "--arch=x86_64", "--command=/app/bin/chrome", "--file-forwarding", "com.google.Chrome", "@@u", "@@", "--ozone-platform=wayland"], launcher.Argv);
            Assert.True(PinnedMatch.Matches("google-chrome", launcher.MatchKeys));
            Assert.True(PinnedMatch.Matches("com.google.Chrome", launcher.MatchKeys));
            var state = new ChromeProfile.State("/home/test", null, null, null, "host", _ => new(ChromeProfile.LockKind.Missing), _ => ChromeProfile.ProcessSession.Unknown);
            var locked = false;
            var registry = new FakeWindowRegistry();
            launcher = launcher with { PrepareLaunch = argv => ChromeProfile.Prepare(argv, state with { ReadLock = path => locked && !path.Contains("rosella-user-data", StringComparison.Ordinal) ? new(ChromeProfile.LockKind.Symlink, "host-123") : new(ChromeProfile.LockKind.Missing) }) };
            launcher.Click(registry);
            locked = true;
            launcher.Click(registry);
            Assert.Equal([.. launcher.Argv, "--disable-background-mode"], registry.Launched[0]);
            Assert.Equal([.. launcher.Argv, "--disable-background-mode", "--user-data-dir=/home/test/.var/app/com.google.Chrome/config/rosella-user-data"], registry.Launched[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("/home/test/wrapper run com.google.Chrome")]
    [InlineData("/usr/bin/flatpak run org.example.Other com.google.Chrome")]
    [InlineData("/usr/bin/flatpak run --unknown com.google.Chrome")]
    [InlineData("google-chrome")]
    [InlineData("/usr/bin/flatpak run --file-forwarding com.google.Chrome --user-data-dir=/custom")]
    public void Resolve_DoesNotSubstituteInvalidFlatpakEntry(string candidate)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-pins-{Guid.NewGuid():N}");
        var user = Path.Combine(root, "user");
        var system = Path.Combine(root, "system");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(system);
        try
        {
            File.WriteAllText(Path.Combine(user, "google-chrome.desktop"), "[Desktop Entry]\nExec=/home/michael/.local/bin/chrome-atomic-launch\n");
            File.WriteAllText(Path.Combine(system, "com.google.Chrome.desktop"), $"[Desktop Entry]\nExec={candidate}\n");
            var launcher = Assert.Single(PinnedApps.Resolve(24, [user, system], [root]));
            Assert.Equal("/home/michael/.local/bin/chrome-atomic-launch", launcher.Argv[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Resolve_RespectsExplicitProfileOnWrapper()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-pins-{Guid.NewGuid():N}");
        var user = Path.Combine(root, "user");
        var system = Path.Combine(root, "system");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(system);
        try
        {
            File.WriteAllText(Path.Combine(user, "google-chrome.desktop"), "[Desktop Entry]\nExec=wrapper --user-data-dir=/custom\n");
            File.WriteAllText(Path.Combine(system, "com.google.Chrome.desktop"), "[Desktop Entry]\nExec=/usr/bin/flatpak run com.google.Chrome\n");
            var launcher = Assert.Single(PinnedApps.Resolve(24, [user, system], [root]));
            Assert.Equal(["wrapper", "--user-data-dir=/custom", "--ozone-platform=wayland"], launcher.Argv);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("--user-data-dir=/custom")]
    public void Resolve_ChatGptFollowsTheDesktopEntryProfile(string profileArgument)
    {
        var root = Directory.CreateTempSubdirectory("rosella-chatgpt-");
        try
        {
            File.WriteAllText(Path.Combine(root.FullName, "chatgpt.desktop"),
                $"[Desktop Entry]\nName=ChatGPT\nExec=chatgpt {profileArgument} %U\n");
            var launcher = Assert.Single(PinnedApps.Resolve(24, [root.FullName], [root.FullName]));
            var registry = new FakeWindowRegistry();
            launcher.Click(registry);
            var expected = profileArgument.Length == 0
                ? new[] { "chatgpt", "--ozone-platform=wayland" }
                : ["chatgpt", profileArgument, "--ozone-platform=wayland"];
            Assert.Equal(expected, Assert.Single(registry.Launched));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void Resolve_UserAlternativeIdBeatsSystemPreferredId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-pins-{Guid.NewGuid():N}");
        var user = Path.Combine(root, "user");
        var system = Path.Combine(root, "system");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(system);
        try
        {
            File.WriteAllText(Path.Combine(user, "com.google.Chrome.desktop"), "[Desktop Entry]\nName=User Chrome\nExec=user-wrapper %U\n");
            File.WriteAllText(Path.Combine(system, "google-chrome.desktop"), "[Desktop Entry]\nName=System Chrome\nExec=google-chrome %U\n");
            var launcher = Assert.Single(PinnedApps.Resolve(24, [user, system], [root]));
            Assert.Equal("com.google.Chrome.desktop", launcher.DesktopId);
            Assert.Equal("user-wrapper", launcher.Argv[0]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("google-chrome.desktop", "google-chrome --ozone-platform=x11 %U", "google-chrome", "--ozone-platform=x11")]
    [InlineData("com.google.Chrome.desktop", "flatpak run --file-forwarding com.google.Chrome @@u %U @@", "flatpak", "@@")]
    public void Resolve_ChromeAppendsWaylandToNativeAndFlatpakEntries(string desktopId, string exec, string executable, string precedingArgument)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-pins-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, desktopId), $"[Desktop Entry]\nName=Google Chrome\nExec={exec}\n");
            var launcher = Assert.Single(PinnedApps.Resolve(24, [root], [root]));
            Assert.Equal(executable, launcher.Argv[0]);
            Assert.Equal(precedingArgument, launcher.Argv[^2]);
            Assert.Equal("--ozone-platform=wayland", launcher.Argv[^1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("org.mozilla.firefox.desktop", "org.mozilla.firefox")]
    [InlineData("firefox.desktop", "firefox")]
    public void Resolve_FirefoxUsesDesktopIconAndMatchesWindow(string desktopId, string iconName)
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-pins-{Guid.NewGuid():N}");
        var apps = Path.Combine(root, "applications");
        var iconDirectory = Path.Combine(root, "icons", "hicolor", "48x48", "apps");
        Directory.CreateDirectory(apps);
        Directory.CreateDirectory(iconDirectory);
        try
        {
            File.WriteAllText(Path.Combine(apps, desktopId), $"[Desktop Entry]\nName=Firefox\nExec=firefox %u\nIcon={iconName}\n");
            var icon = Path.Combine(iconDirectory, $"{iconName}.png");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "icon.png"), icon);
            var launcher = Assert.Single(PinnedApps.Resolve(24, [apps], [root]));
            Assert.Equal(["firefox"], launcher.Argv);
            Assert.Equal(icon, launcher.IconPath);
            Assert.NotNull(launcher.Icon);
            Assert.True(PinnedMatch.Matches("firefox", launcher.MatchKeys));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void MatchKeys_AreClassIdAndLastSegment()
    {
        Assert.Equal(["Chrome", "google-chrome"], PinnedMatch.MatchKeysFor("google-chrome.desktop", "Chrome"));
        Assert.Equal(["org.gnome.Nautilus", "Nautilus"], PinnedMatch.MatchKeysFor("org.gnome.Nautilus.desktop", ""));
        Assert.Equal(["code"], PinnedMatch.MatchKeysFor("code.desktop", "code"));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveAcrossTheKeys()
    {
        var keys = PinnedMatch.MatchKeysFor("google-chrome.desktop", "Google-chrome");
        Assert.True(PinnedMatch.Matches("google-chrome", keys));
        Assert.True(PinnedMatch.Matches("Google-Chrome", keys));
        Assert.False(PinnedMatch.Matches("firefox", keys));
        Assert.False(PinnedMatch.Matches("", keys));
        Assert.False(PinnedMatch.Matches(null, keys));
    }

    [Fact]
    public void Click_FocusesTheMatchingWindow()
    {
        var launcher = new PinnedLauncher("Files", "nautilus.desktop", ["nautilus"], ["nautilus"], null, null);
        var window = new SurfaceHandler(new Scene());
        var registry = new FakeWindowRegistry { Match = window };

        launcher.Click(registry);

        Assert.Equal([window], registry.Focused);
        Assert.Empty(registry.Launched);
    }

    [Fact]
    public void Click_LaunchesWhenNoWindowMatches()
    {
        var launcher = new PinnedLauncher("Steam", "steam.desktop", ["steam"], ["/usr/games/steam", "-silent"], null, null);
        var registry = new FakeWindowRegistry();

        launcher.Click(registry);

        Assert.Empty(registry.Focused);
        Assert.Equal(0, registry.RecoveryRequests);
        Assert.Single(registry.Launched);
        Assert.Equal(["/usr/games/steam", "-silent"], registry.Launched[0]);
    }

    [Fact]
    public void Click_QueuesChatGptRecoveryBeforeLaunching()
    {
        var launcher = new PinnedLauncher("ChatGPT", "chatgpt.desktop", ["chatgpt"], ["chatgpt"], null, null,
            argv => [.. argv, "--ozone-platform=wayland"]);
        var registry = new FakeWindowRegistry { CanRecover = true };
        var replacements = 0;

        launcher.Click(registry, recovered: _ => replacements++);

        Assert.Equal(1, registry.RecoveryRequests);
        Assert.Empty(registry.Launched);
        Assert.NotNull(registry.Relaunch);
        registry.Relaunch();
        Assert.Equal(["chatgpt", "--ozone-platform=wayland"], Assert.Single(registry.Launched));
        Assert.Equal(1, replacements);
    }

    [Fact]
    public void Click_LaunchesChatGptNormallyWithoutAResidual()
    {
        var launcher = new PinnedLauncher("ChatGPT", "chatgpt.desktop", ["chatgpt"], ["chatgpt"], null, null);
        var registry = new FakeWindowRegistry();

        launcher.Click(registry);

        Assert.Equal(1, registry.RecoveryRequests);
        Assert.Equal(["chatgpt"], Assert.Single(registry.Launched));
    }

    [Fact]
    public void TileLetter_IsTheFirstLetterOfTheName()
    {
        var launcher = new PinnedLauncher("Google Chrome", "google-chrome.desktop", [], [], null, null);
        Assert.Equal('G', launcher.TileLetter);
    }

    [Fact]
    public void StartupApps_UsesSavedDockSelectionAndPreparesLaunchOnlyOnce()
    {
        var directory = Directory.CreateTempSubdirectory("rosella-startup-");
        try
        {
            var config = Path.Combine(directory.FullName, "startup-apps");
            var chrome = new PinnedLauncher("Browser", "browser.desktop", [], ["browser"], null, null,
                argv => [.. argv, "--wayland"]);
            var editor = new PinnedLauncher("Editor", "editor.desktop", [], ["editor"], null, null);
            new StartupApps(config).Toggle(chrome.DesktopId);

            var loaded = new StartupApps(config);
            var registry = new FakeWindowRegistry();
            Assert.True(loaded.Contains(chrome.DesktopId));
            loaded.Launch([chrome, editor, chrome], registry);
            Assert.Equal(["browser", "--wayland"], Assert.Single(registry.Launched));

            loaded.Toggle(chrome.DesktopId);
            registry.Launched.Clear();
            new StartupApps(config).Launch([chrome, editor], registry);
            Assert.Empty(registry.Launched);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeWindowRegistry : IWindowRegistry
    {
        public SurfaceHandler? Match { get; init; }

        public bool CanRecover { get; init; }

        public Action? Relaunch { get; private set; }

        public List<SurfaceHandler> Focused { get; } = [];

        public List<string[]> Launched { get; } = [];

        public int RecoveryRequests { get; private set; }

        public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys) => Match;

        public bool TryRecoverChatGpt(IReadOnlyList<string> matchKeys, Action relaunch, Action? failed)
        {
            RecoveryRequests++;
            if (!CanRecover)
            {
                return false;
            }

            Relaunch = relaunch;
            return true;
        }

        public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys) => Focused.Add(surface);

        public int? Launch(IReadOnlyList<string> argv) { Launched.Add([.. argv]); return null; }
    }
}
