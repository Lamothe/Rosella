using System.Runtime.Loader;
using System.Security.Cryptography;

namespace Rosella.Compositor;

internal sealed record DashboardWidgetGeneration(IDashboardWidget Widget, ISettingsPanel? Settings = null,
    AssemblyLoadContext? Context = null, string? Build = null);

/// <summary>A paint can hold one snapshot while input publishes a replacement.</summary>
internal sealed class DashboardWidgetSet(DashboardWidgetGeneration[] widgets)
{
    internal IDashboardWidget this[DashboardPage page] => Generation(page).Widget;
    internal ISettingsPanel? Settings => Generation(DashboardPage.Settings).Settings;
    internal DashboardWidgetGeneration Generation(DashboardPage page) => widgets[(int)page];

    internal DashboardWidgetSet Replace(IReadOnlyList<(DashboardPage Page, DashboardWidgetGeneration Generation)> replacements)
    {
        var next = (DashboardWidgetGeneration[])widgets.Clone();
        foreach (var (page, generation) in replacements)
        {
            next[(int)page] = generation;
        }
        return new DashboardWidgetSet(next);
    }
}

internal sealed class DashboardWidgetModules
{
    private const string AssemblyName = "Rosella.DashboardWidgets";
    internal const string AssemblyFile = AssemblyName + ".dll";
    internal static string PublishedPath(string directory) => Path.Combine(directory, "modules", "dashboard", "v1", AssemblyFile);

    internal static IReadOnlyList<(DashboardPage Page, string Id, string Label)> Entries { get; } =
    [
        (DashboardPage.Applications, "dashboard-apps", "App grid"),
        (DashboardPage.Weather, "dashboard-weather", "Weather"),
        (DashboardPage.Calendar, "dashboard-calendar", "Calendar"),
        (DashboardPage.Notifications, "dashboard-notifications", "Notifications"),
        (DashboardPage.Settings, "dashboard-settings", "System settings"),
    ];

    private volatile DashboardWidgetSet _active = new([
        new(new AppsWidget()), new(new WeatherWidget()), new(new CalendarWidget()), new(new NotificationsWidget()), new(new SettingsWidget()),
    ]);
    private readonly string?[] _lastReload = new string?[DashboardLayout.TabCount];
    private string? _path;
    private ISystemActions? _actions;
    private IVolumeControl? _volume;
    private Action<ISettingsPanel>? _prepareSettings;

    internal DashboardWidgetSet Active => _active;
    internal bool Available => _path is not null;

    internal void Configure(string path, ISystemActions actions, IVolumeControl volume, Action<ISettingsPanel> prepareSettings)
    {
        _path = path;
        _actions = actions;
        _volume = volume;
        _prepareSettings = prepareSettings;
    }

    internal (string Build, string? Result) ReadStatus(DashboardPage page)
    {
        var build = Active.Generation(page).Build;
        return (build is null ? "Built-in" : $"Build {build[..12]}", Volatile.Read(ref _lastReload[(int)page]));
    }

    internal string Describe()
    {
        var installed = "unavailable";
        if (_path is not null)
        {
            try
            {
                installed = TaskbarGeneration.InstalledBuild(_path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                installed = error.Message;
            }
        }
        var active = Active;
        return string.Join('\n', Entries.Select(entry =>
            $"{entry.Id}  {entry.Label}\n  active: {active.Generation(entry.Page).Build ?? "built-in"}\n" +
            $"  installed: {installed}\n  last reload: {Volatile.Read(ref _lastReload[(int)entry.Page]) ?? "none"}"));
    }

    internal string Reload(DashboardPage? widget)
    {
        if (_path is null)
        {
            throw new InvalidOperationException("Dashboard widget reload is unavailable");
        }

        DashboardPage[] pages = widget is { } page ? [page] : Entries.Select(entry => entry.Page).ToArray();
        var loaded = new List<(DashboardPage Page, DashboardWidgetGeneration Generation)>();
        var previous = Active;
        string build;
        try
        {
            var bytes = File.ReadAllBytes(_path);
            build = Convert.ToHexStringLower(SHA256.HashData(bytes));
            foreach (var target in pages)
            {
                loaded.Add((target, Load(target, bytes, build)));
            }
            var next = previous.Replace(loaded);
            if (pages.Contains(DashboardPage.Settings))
            {
                _prepareSettings!(next.Settings!);
            }
            _active = next;
        }
        catch (Exception error)
        {
            foreach (var (_, generation) in loaded)
            {
                generation.Context?.Unload();
            }
            foreach (var target in pages)
            {
                Volatile.Write(ref _lastReload[(int)target], "Reload failed · active build kept");
            }
            throw new InvalidOperationException($"Could not load dashboard widget from {_path}: {error.GetBaseException().Message}", error);
        }

        foreach (var target in pages)
        {
            var retired = previous.Generation(target);
            Volatile.Write(ref _lastReload[(int)target], build == retired.Build ? "Same build reloaded" : "New build loaded");
            retired.Context?.Unload();
        }
        var name = widget is null ? "Dashboard widgets" : Entries.First(entry => entry.Page == widget).Label;
        return $"{name} reloaded · Build {build[..12]}";
    }

    private DashboardWidgetGeneration Load(DashboardPage page, byte[] bytes, string build)
    {
        // Contexts are independent even though the release packages all widget types together.
        var context = new AssemblyLoadContext(AssemblyName + "." + page, isCollectible: true);
        try
        {
            using var file = new MemoryStream(bytes, writable: false);
            var assembly = context.LoadFromStream(file);
            var type = page == DashboardPage.Applications ? "AppsWidget" : page + "Widget";
            var entry = assembly.GetType(AssemblyName + "." + type, throwOnError: true)!;
            var implementation = (IDashboardWidget)Activator.CreateInstance(entry)!;
            var settings = page == DashboardPage.Settings
                ? ((IDashboardSettingsWidget)implementation).CreateSettings(_actions!, _volume!) : null;
            return new DashboardWidgetGeneration(implementation, settings, context, build);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    internal void Unload()
    {
        foreach (var entry in Entries)
        {
            Active.Generation(entry.Page).Context?.Unload();
        }
    }
}
