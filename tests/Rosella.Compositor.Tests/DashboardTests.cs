namespace Rosella.Compositor.Tests;

public class DashboardTests
{
    [Theory]
    [InlineData(1280, 900)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    public void Layout_AppRowsFillTheHeightOfTheSidebar(int width, int height)
    {
        var layout = DashboardLayout.Create(width, height);
        Assert.False(layout.Compact);
        Assert.Equal(layout.Weather.Y, layout.Applications.Y);
        var bottom = layout.Settings.Y + layout.Settings.H;
        Assert.Equal(bottom, layout.Applications.Y + layout.Applications.H);

        var last = layout.Tile(layout.PageSize - 1);
        Assert.InRange(bottom - (last.Y + last.H), 1, 24);
        Assert.Equal(layout.PageSize - 1, layout.TileAt(last.X + 1, last.Y + last.H - 1));
    }

    [Fact]
    public void Backdrop_BlursToTheScreenEdgesWithABrighterInset()
    {
        const int width = 320, height = 240;
        const uint dark = 0xFF404040, light = 0xFFC0C0C0;
        var wallpaper = Enumerable.Range(0, width * height).Select(index => index % 2 == 0 ? dark : light).ToArray();
        var pixels = new uint[wallpaper.Length];
        Background.SetWallpaper(wallpaper, width, height);
        try
        {
            var dashboard = new Dashboard();
            dashboard.DrawBackdrop(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()),
                width, height, width * 4, 1, DamageRect.For(width, height));

            Assert.All(new[] { 0, width - 1, width * (height - 1), width * height - 1 }, index =>
                Assert.InRange(pixels[index], dark + 1, light - 1));
            // The source stripes average approximately 50% grey. The inset
            // must be lighter than the source average as well as the rim.
            Assert.True(pixels[80 * width + 160] > 0xFF808080u);
            Assert.True(pixels[80 * width + 160] > pixels[160]);
        }
        finally
        {
            Background.ClearWallpaper();
        }
    }

    [Fact]
    public void Reload_ReplacesOnlyTheSelectedWidgetAndKeepsTheDashboardOpen()
    {
        using var directory = new TempDirectories();
        var path = Path.Combine(directory.User, DashboardWidgetModules.AssemblyFile);
        File.Copy(Path.Combine(AppContext.BaseDirectory, DashboardWidgetModules.AssemblyFile), path);
        var shell = new Shell();
        var widgets = shell.Dashboard.Widgets;
        shell.ConfigureDashboardWidgets(path);
        var original = widgets.Active;
        var originalSettings = shell.Settings;
        var taskbar = shell.Taskbar;
        var layout = DashboardLayout.Create(1920, 1080);
        Assert.Equal(layout.Calendar.W, originalSettings.PanelRect(default, layout.Settings.W, layout.Settings.H).W);
        var card = shell.Dashboard.SettingsPanel(layout, originalSettings);
        Assert.Equal((layout.Calendar.X, layout.Calendar.W), (card.X, card.W));
        Assert.Equal(layout.Settings.Y, card.Y);
        Assert.All(DashboardWidgetModules.Entries, entry => Assert.True(original.Generation(entry.Page).Context?.IsCollectible));
        Assert.Equal(5, DashboardWidgetModules.Entries.Select(entry => original.Generation(entry.Page).Context).Distinct().Count());
        shell.Dashboard.Open(DashboardPage.Settings);
        shell.Settings.OpenModules();
        var controls = shell.Settings.Modules!;
        controls.Step(1);
        controls.Step(1);
        Assert.Equal("Calendar", controls.Selected.Label);

        var panel = shell.Settings.PanelRect(new Rect(700, 540, 100, 40), 800, 600);
        var reload = ModulePicker.ReloadButton(panel, choices: true);
        Assert.True(shell.Settings.HandlePointer(reload.X + 5, reload.Y + 5, panel, pressed: true));

        Assert.NotSame(original[DashboardPage.Calendar], widgets.Active[DashboardPage.Calendar]);
        Assert.Same(original[DashboardPage.Weather], widgets.Active[DashboardPage.Weather]);
        Assert.Same(originalSettings, shell.Settings);
        Assert.Same(taskbar, shell.Taskbar);
        Assert.True(shell.Dashboard.IsOpen);
        Assert.Equal(DashboardPage.Settings, shell.Dashboard.Page);
        Assert.True(shell.Settings.ModulesOpen);
        Assert.Equal("Same build reloaded", controls.ReadStatus().Result);

        shell.ReloadDashboardWidgets(DashboardPage.Settings);
        Assert.NotSame(originalSettings, shell.Settings);
        Assert.Same(controls, shell.Settings.Modules);
        Assert.True(shell.Settings.ModulesOpen);

        var active = widgets.Active;
        File.WriteAllText(path, "invalid dashboard assembly");
        Assert.Throws<InvalidOperationException>(() => shell.ReloadDashboardWidgets(null));
        Assert.Same(active, widgets.Active);
        Assert.Contains("Reload failed", shell.DescribeModules());
        widgets.Unload();
    }

    [Fact]
    public void UntintedGlass_SoftensEdgesWithoutDarkeningSolidColours()
    {
        const int width = 22, height = 14;
        var source = Enumerable.Range(0, width * height)
            .Select(index => index % width < width / 2 ? 0xFFFF8040u : 0xFF0040FFu).ToArray();
        var output = new uint[source.Length];
        var glass = new FrostedGlass(tinted: false);
        var area = new Rect(0, 0, width, height);
        glass.Prepare(System.Runtime.InteropServices.MemoryMarshal.AsBytes(source.AsSpan()),
            width, height, width * 4, 2, area.ToDamage(1));
        glass.Draw(System.Runtime.InteropServices.MemoryMarshal.AsBytes(output.AsSpan()), width * 4, area, 0, 1);

        Assert.Equal(source[width * 7 + 4], output[width * 7 + 4]);
        Assert.Equal(source[width * 7 + 17], output[width * 7 + 17]);
        Assert.NotEqual(source[width * 7 + 10], output[width * 7 + 10]);
        Assert.NotEqual(source[width * 7 + 11], output[width * 7 + 10]);
    }

    [Fact]
    public void AntialiasedGlass_BlendIsConfinedToTheRoundedEdge()
    {
        const int width = 48, height = 32;
        const uint wallpaper = 0xFF505050;
        var source = Enumerable.Repeat(wallpaper, width * height).ToArray();
        var output = (uint[])source.Clone();
        var glass = new FrostedGlass(tinted: false);
        var panel = new Rect(4, 5, 40, 22);
        var damage = panel.ToDamage(1);
        glass.Prepare(System.Runtime.InteropServices.MemoryMarshal.AsBytes(source.AsSpan()), width, height, width * 4, 3, damage);
        glass.Draw(System.Runtime.InteropServices.MemoryMarshal.AsBytes(output.AsSpan()), width * 4, panel, 8, 1, damage,
            antialiasEdge: true);

        var top = panel.Y * width + panel.X;
        Assert.Equal(wallpaper, output[top]);
        Assert.Equal(wallpaper, output[top + 11 * width + 20]);
        Assert.True(wallpaper < output[top + 5] && output[top + 5] < output[top + 20]);
        Assert.Equal(output[top + 5], output[top + panel.W - 1 - 5]);
        Assert.Equal(wallpaper, output[top - width + 20]);
    }

    [Fact]
    public void Calendar_NavigatesAcrossYearsAndReturnsToThisMonth()
    {
        var calendar = new DashboardCalendar();
        var panel = new Rect(10, 30, 350, 232);
        var today = new DateOnly(2026, 1, 2);
        Assert.True(calendar.Click(panel, panel.X + 20, panel.Y + 20));
        Assert.Equal(new DateOnly(2025, 12, 1), calendar.Month(today));
        Assert.True(calendar.Click(panel, panel.X + 160, panel.Y + 20));
        Assert.Equal(new DateOnly(2026, 1, 1), calendar.Month(today));
    }

    [Fact]
    public void UpdateHistory_ListsSuccessfulUpgradesAndSkipsFlatpakRuntimes()
    {
        var rpm = DashboardUpdates.ParseDnf("""
            [
              {"status":"Ok","end_time":1795686352,"packages":[{"action":"Upgrade","nevra":"pipewire-libs-1.4.9-2.fc45.x86_64"},{"action":"Upgraded","nevra":"pipewire-libs-1.4.8-1.fc45.x86_64"}]},
              {"status":"Error","end_time":1795686353,"packages":[{"action":"Upgrade","nevra":"firefox-146.0-1.fc45.x86_64"}]}
            ]
            """);
        Assert.Equal("pipewire-libs", Assert.Single(rpm).Body);
        var flatpak = DashboardUpdates.ParseFlatpak("""
            [
              {"time":"Nov 26 17:52:15","change":"deploy update","application":"org.gimp.GIMP","ref":"app/org.gimp.GIMP/x86_64/stable"},
              {"time":"Nov 26 17:52:16","change":"deploy update","application":"org.gimp.GIMP.Locale","ref":"runtime/org.gimp.GIMP.Locale/x86_64/stable"}
            ]
            """, new DateOnly(2026, 11, 26));
        Assert.Equal("GIMP", Assert.Single(flatpak).Body);
    }

    [Fact]
    public void NotificationHistory_KeepsRecentMessagesAfterTheToastCloses()
    {
        var notifications = new NotificationStack();
        var key = notifications.Show(default, "Files", "Copy complete", "Two files", 5000);

        Assert.True(notifications.Close(key));
        Assert.Equal("Copy complete", Assert.Single(notifications.Recent()).Title);
    }

    [Fact]
    public void AppList_UsesTheFirstDesktopEntryAndHidesNonLaunchableApps()
    {
        using var directory = new TempDirectories();
        File.WriteAllText(Path.Combine(directory.User, "hidden.desktop"),
            "[Desktop Entry]\nName=Hidden\nHidden=true\n");
        File.WriteAllText(Path.Combine(directory.System, "hidden.desktop"),
            "[Desktop Entry]\nName=Hidden in user directory\nExec=hidden\n");
        File.WriteAllText(Path.Combine(directory.System, "broken.desktop"),
            "[Desktop Entry]\nName=Broken\n");
        File.WriteAllText(Path.Combine(directory.System, "reader.desktop"),
            "[Desktop Entry]\nName=Reader\nExec=reader %u\n");
        File.WriteAllText(Path.Combine(directory.User, "notes.desktop"),
            "[Desktop Entry]\nName=Notes\nExec=notes\nIcon=notes\n");

        var apps = DashboardApp.Installed([directory.User, directory.System]);

        Assert.Equal(["Notes", "Reader"], apps.Select(app => app.Name));
        Assert.Equal("notes", apps[0].Icon);
    }

    [Fact]
    public void Weather_ReadsLocalConditionsAndTodaysRange()
    {
        var location = DashboardWeather.ReadLocation("""{"name":"Test City","latitude":-35.17,"longitude":149.13}""");
        Assert.Equal(new WeatherLocation("Test City", -35.17, 149.13), location);
        Assert.Null(DashboardWeather.ReadLocation("""{"name":"Invalid","latitude":91,"longitude":149}"""));

        var forecast = DashboardWeather.ReadForecast("""
            {
              "current":{"temperature_2m":19.4,"apparent_temperature":17.8,"weather_code":80,"is_day":1},
              "daily":{"temperature_2m_max":[24.2],"temperature_2m_min":[13.1]}
            }
            """);
        Assert.Equal("Showers", forecast.Conditions);
        Assert.Equal("19°", WeatherForecast.Degrees(forecast.Temperature));
        Assert.Equal(24.2, forecast.High);
        Assert.Equal(13.1, forecast.Low);
    }

    private sealed class TempDirectories : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("rosella-dashboard-");
        internal string User => Directory.CreateDirectory(Path.Combine(_root.FullName, "user")).FullName;
        internal string System => Directory.CreateDirectory(Path.Combine(_root.FullName, "system")).FullName;
        public void Dispose() => _root.Delete(recursive: true);
    }
}
