namespace Rosella.Compositor.Tests;

public class PinnedConfigurationTests
{
    [Fact]
    public void SelectPath_PrefersMachineFileThenSharedFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-launchers-{Guid.NewGuid():N}");
        var directory = Path.Combine(root, "rosella");
        Directory.CreateDirectory(directory);
        try
        {
            var shared = Path.Combine(directory, "launchers.json");
            var silver = Path.Combine(directory, "launchers.silver.json");
            Assert.Null(PinnedConfiguration.SelectPath("/home/test", root, "silver"));
            File.WriteAllText(shared, "[]");
            File.WriteAllText(silver, "[]");
            Assert.Equal(silver, PinnedConfiguration.SelectPath("/home/test", root, "silver"));
            Assert.Equal(shared, PinnedConfiguration.SelectPath("/home/test", root, "mercury"));
            Assert.Equal(shared, PinnedConfiguration.SelectPath("/home/test", root, "../silver"));
            Assert.Null(PinnedConfiguration.SelectPath(root, "relative", "silver"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_ParsesOrderedIdsAlternativesAndEmptyList()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "[\"one.desktop\", [\"two.desktop\", \"three.desktop\"]]");
            var groups = Assert.IsType<string[][]>(PinnedConfiguration.Load(path));
            Assert.Equal(2, groups.Length);
            Assert.Equal(["one.desktop"], groups[0]);
            Assert.Equal(["two.desktop", "three.desktop"], groups[1]);
            File.WriteAllText(path, "[]");
            Assert.Empty(Assert.IsType<string[][]>(PinnedConfiguration.Load(path)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[\"\\uD800.desktop\"]")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    [InlineData("[\"../app.desktop\"]")]
    [InlineData("[\"/app.desktop\"]")]
    [InlineData("[\".desktop\"]")]
    [InlineData("[\"app\"]")]
    [InlineData("[\"app.desktop\", \"app.desktop\"]")]
    [InlineData("[[\"app.desktop\", 4]]")]
    public void Load_InvalidFileFallsBackToDefaults(string content)
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, content);
            Assert.Null(PinnedConfiguration.Load(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Load_OversizedFileFallsBackToDefaults()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, new string(' ', 65537));
            Assert.Null(PinnedConfiguration.Load(path));
            Assert.Null(PinnedConfiguration.Load(null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Resolve_ConfiguredOrderAlternativesAndChromePolicy()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rosella-launchers-{Guid.NewGuid():N}");
        var user = Path.Combine(root, "user");
        var system = Path.Combine(root, "system");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(system);
        try
        {
            File.WriteAllText(Path.Combine(system, "preferred.desktop"), "[Desktop Entry]\nName=System\nExec=system\n");
            File.WriteAllText(Path.Combine(user, "alternative.desktop"), "[Desktop Entry]\nName=User\nExec=user\n");
            File.WriteAllText(Path.Combine(user, "com.google.Chrome.desktop"), "[Desktop Entry]\nName=Browser\nExec=google-chrome --ozone-platform=x11\n");
            File.WriteAllText(Path.Combine(user, "custom-browser.desktop"), "[Desktop Entry]\nName=Custom Browser\nExec=google-chrome --user-data-dir=/custom\n");
            File.WriteAllText(Path.Combine(user, "env-browser.desktop"), "[Desktop Entry]\nName=Env Browser\nExec=env FOO=bar google-chrome\n");
            var launchers = PinnedApps.Resolve(24, [user, system], [root],
                [["com.google.Chrome.desktop"], ["preferred.desktop", "alternative.desktop"], ["missing.desktop"]]);
            Assert.Equal(["Browser", "User"], launchers.Select(item => item.Name));
            Assert.Equal("--ozone-platform=wayland", launchers[0].Argv[^1]);
            Assert.NotNull(launchers[0].PrepareLaunch);
            Assert.Equal("alternative.desktop", launchers[1].DesktopId);
            Assert.Null(launchers[1].PrepareLaunch);
            var custom = Assert.Single(PinnedApps.Resolve(24, [user, system], [root], [["custom-browser.desktop"]]));
            Assert.Equal("--ozone-platform=wayland", custom.Argv[^1]);
            Assert.NotNull(custom.PrepareLaunch);
            Assert.Equal(custom.Argv, custom.PrepareLaunch!(custom.Argv));
            var env = Assert.Single(PinnedApps.Resolve(24, [user, system], [root], [["env-browser.desktop"]]));
            Assert.Equal("--ozone-platform=wayland", env.Argv[^1]);
            Assert.NotNull(env.PrepareLaunch);
            Assert.Empty(PinnedApps.Resolve(24, [user, system], [root], []));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
