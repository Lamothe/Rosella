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
    public void Table_IsTheSixLaunchersInOrder()
    {
        Assert.Equal(
            ["Google Chrome", "Terminal", "Files", "Visual Studio Code", "Steam", "Resources"],
            PinnedApps.Table.Select(entry => entry.Name));
    }

    [Fact]
    public void Table_CandidatesMatchTheSpec()
    {
        Assert.Equal(
            ["google-chrome.desktop", "google-chrome-stable.desktop", "com.google.Chrome.desktop"],
            PinnedApps.Table[0].DesktopIds);
        Assert.Equal(
            ["org.gnome.Ptyxis.desktop", "org.gnome.Console.desktop", "gnome-terminal.desktop", "org.gnome.Terminal.desktop"],
            PinnedApps.Table[1].DesktopIds);
        Assert.Equal(["org.gnome.Nautilus.desktop", "nautilus.desktop"], PinnedApps.Table[2].DesktopIds);
        Assert.Equal(["com.visualstudio.code.desktop", "com.microsoft.VSCode.desktop", "code.desktop", "code-oss.desktop"], PinnedApps.Table[3].DesktopIds);
        Assert.Equal(["com.valvesoftware.Steam.desktop", "steam.desktop"], PinnedApps.Table[4].DesktopIds);
        Assert.Equal(["net.nokyan.Resources.desktop", "org.gnome.World.Resources.desktop", "resources.desktop"], PinnedApps.Table[5].DesktopIds);
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
        Assert.Single(registry.Launched);
        Assert.Equal(["/usr/games/steam", "-silent"], registry.Launched[0]);
    }

    [Fact]
    public void TileLetter_IsTheFirstLetterOfTheName()
    {
        var launcher = new PinnedLauncher("Google Chrome", "google-chrome.desktop", [], [], null, null);
        Assert.Equal('G', launcher.TileLetter);
    }

    private sealed class FakeWindowRegistry : IWindowRegistry
    {
        public SurfaceHandler? Match { get; init; }

        public List<SurfaceHandler> Focused { get; } = [];

        public List<string[]> Launched { get; } = [];

        public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys) => Match;

        public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys) => Focused.Add(surface);

        public void Launch(IReadOnlyList<string> argv) => Launched.Add([.. argv]);
    }
}
