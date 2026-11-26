using System.Collections.Concurrent;
using static Rosella.Compositor.DashboardWidgetChrome;

namespace Rosella.Compositor;

internal enum DashboardPage { Applications, Weather, Calendar, Notifications, Settings }

internal readonly record struct DashboardLayout(Rect Glass, Rect Applications, Rect Weather, Rect Calendar,
    Rect Notifications, Rect Settings, Rect Tabs)
{
    private const int GridPadding = 8;
    private const int MinimumGridRowHeight = 102;
    internal const int TabCount = 5;

    internal bool Compact => Tabs.W > 0;
    internal int Columns => Math.Max(1, (Applications.W - 2 * GridPadding - 12) / 108);
    internal int Rows => Math.Max(0, (Applications.H - 2 * GridPadding) / MinimumGridRowHeight);
    internal int PageSize => Columns * Rows;
    internal int MaximumFirstRow(int count) => Math.Max(0, (count + Columns - 1) / Columns - Rows);
    private int ColumnWidth => (Applications.W - 2 * GridPadding - 12) / Columns;
    private int RowHeight => Rows == 0 ? MinimumGridRowHeight : (Applications.H - 2 * GridPadding) / Rows;
    internal Rect Tile(int index) => new(Applications.X + GridPadding + index % Columns * ColumnWidth,
        Applications.Y + GridPadding + index / Columns * RowHeight, ColumnWidth - 6, RowHeight - 6);
    internal int TileAt(int x, int y)
    {
        var start = Tile(0);
        if (x < start.X || y < start.Y || ColumnWidth <= 0)
        {
            return -1;
        }
        var column = (x - start.X) / ColumnWidth;
        var row = (y - start.Y) / RowHeight;
        var index = row * Columns + column;
        return row < Rows && column < Columns && Tile(index).Contains(x, y) ? index : -1;
    }

    internal Rect Tab(DashboardPage page) =>
        new(Tabs.X + (int)page * (Tabs.W / TabCount), Tabs.Y, Tabs.W / TabCount - 4, Tabs.H);

    internal static DashboardLayout Create(int width, int height)
    {
        var margin = width >= 1000 && height >= 660 ? 24 : 10;
        var glass = new Rect(margin, margin, Math.Max(0, width - 2 * margin),
            Math.Max(0, height - Shell.BarHeight - 16 - margin));

        if (width < 850 || glass.H < 750)
        {
            var tabs = new Rect(glass.X + 12, glass.Y + 12, Math.Max(0, glass.W - 24), 36);
            var body = new Rect(tabs.X, tabs.Y + 46, tabs.W, Math.Max(0, glass.H - 72));
            return new(glass, body, body, body, body, body, tabs);
        }

        const int sidebar = 350;
        const int gap = 14;
        var contentWidth = Math.Min(1340, glass.W - 64);
        var left = (width - contentWidth) / 2;
        var top = glass.Y + 24;
        var apps = new Rect(left, top, contentWidth - sidebar - 24, glass.H - 48);
        var weather = new Rect(apps.X + apps.W + 24, top, sidebar, 154);
        var calendar = new Rect(weather.X, weather.Y + weather.H + gap, sidebar, 232);
        const int minimumNotificationsHeight = 148;
        const int maximumSettingsHeight = 352;
        var notificationsY = calendar.Y + calendar.H + gap;
        var space = apps.Y + apps.H - notificationsY - gap;
        var settingsHeight = Math.Min(maximumSettingsHeight, space - minimumNotificationsHeight);
        var notifications = new Rect(weather.X, notificationsY, sidebar, space - settingsHeight);
        var settings = new Rect(weather.X, notifications.Y + notifications.H + gap, sidebar, settingsHeight);
        return new(glass, apps, weather, calendar, notifications, settings, default);
    }
}

internal sealed class Dashboard
{
    internal const int NotificationRowHeight = 50;

    private byte[] _backdrop = [];
    private readonly Func<DashboardApp[]> _installed;
    private readonly Func<string, int, IconImage?> _icon;
    internal DashboardWidgetModules Widgets { get; } = new();
    private (int Width, int Height, int Scale, long Wallpaper)? _prepared;
    private readonly ConcurrentDictionary<(string Icon, int Size), Lazy<Task<IconImage?>>> _icons = [];
    private readonly SemaphoreSlim _iconLoads = new(4);
    private volatile DashboardApp[]? _apps;
    private volatile bool _open;
    private volatile DashboardPage _page;
    private volatile int _firstRow;
    private volatile int _notificationOffset;
    private volatile int _keyboardIndex = -1;
    private volatile DashboardPinMenu? _menu;
    private volatile int _settingsScroll;
    private int _scrollRemainder;
    private int _loading;

    internal Dashboard(Func<DashboardApp[]>? installed = null, DashboardWeather? weather = null, DashboardUpdates? updates = null)
    {
        _icon = Icon;
        _installed = installed ?? (() => DashboardApp.Installed(DesktopLocator.DefaultSearchDirs()));
        Weather = weather ?? new DashboardWeather();
        Weather.Dirty = Notify;
        Updates = updates ?? new DashboardUpdates();
        Updates.Dirty = Notify;
    }

    internal DashboardWeather Weather { get; }
    internal DashboardCalendar Calendar { get; } = new();
    internal DashboardUpdates Updates { get; }
    internal Action? Dirty { get; set; }
    internal bool IsOpen => _open;
    internal bool MenuOpen => _menu is not null;
    internal Rect MenuBounds => _menu?.Bounds ?? default;
    internal bool HitMenu((int X, int Y) pointer) => _menu?.Bounds.Contains(pointer.X, pointer.Y) == true;
    internal DashboardPage Page => _page;

    internal void Open(DashboardPage page = DashboardPage.Applications)
    {
        _firstRow = 0;
        _notificationOffset = 0;
        Calendar.Reset();
        _keyboardIndex = -1;
        _scrollRemainder = 0;
        _settingsScroll = 0;
        _menu = null;
        _page = page;
        _open = true;
        Weather.Refresh();
        Updates.Refresh();
        // Desktop entries can be installed or removed while the shell runs.
        if (Interlocked.Exchange(ref _loading, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _apps = _installed();
                }
                finally
                {
                    Volatile.Write(ref _loading, 0);
                    Notify();
                }
            });
        }
    }

    internal void Close()
    {
        _open = false;
        _menu = null;
    }

    private void Notify()
    {
        if (_open)
        {
            Dirty?.Invoke();
        }
    }

    internal void SelectPage(DashboardPage page)
    {
        _settingsScroll = 0;
        _page = page;
        _menu = null;
    }

    internal bool TrySelectTab(DashboardLayout layout, int x, int y)
    {
        if (!layout.Compact || !layout.Tabs.Contains(x, y))
        {
            return false;
        }

        var tab = (x - layout.Tabs.X) / Math.Max(1, layout.Tabs.W / DashboardLayout.TabCount);
        SelectPage((DashboardPage)Math.Min(tab, (int)DashboardPage.Settings));
        return true;
    }

    internal bool Scroll(DashboardLayout layout, int x, int y, int value)
    {
        if ((layout.Compact && Page != DashboardPage.Applications) || !layout.Applications.Contains(x, y) || value == 0)
        {
            return false;
        }

        _scrollRemainder += value;
        var rows = _scrollRemainder / 10;
        _scrollRemainder %= 10;
        var previous = _firstRow;
        var menuWasOpen = _menu is not null;
        var hadSelection = _keyboardIndex >= 0;
        _firstRow = Math.Clamp(previous + rows, 0, layout.MaximumFirstRow(_apps?.Length ?? 0));
        _menu = null;
        _keyboardIndex = -1;
        return _firstRow != previous || menuWasOpen || hadSelection;
    }

    internal DashboardApp? AppAt(DashboardLayout layout, int x, int y)
    {
        if ((layout.Compact && Page != DashboardPage.Applications) || _apps is not { } apps ||
            layout.TileAt(x, y) is not (>= 0 and var tile))
        {
            return null;
        }

        var index = Math.Clamp(_firstRow, 0, layout.MaximumFirstRow(apps.Length)) * layout.Columns + tile;
        return index < apps.Length ? apps[index] : null;
    }

    internal Rect HoverTile(DashboardLayout layout, (int X, int Y) pointer) =>
        AppAt(layout, pointer.X, pointer.Y) is not null ? layout.Tile(layout.TileAt(pointer.X, pointer.Y)) : default;

    internal void MoveSelection(DashboardLayout layout, int amount)
    {
        if (_apps is not { Length: > 0 } apps || layout.PageSize == 0 ||
            (layout.Compact && Page != DashboardPage.Applications))
        {
            return;
        }

        _menu = null;
        _keyboardIndex = _keyboardIndex < 0
            ? Math.Min(_firstRow * layout.Columns, apps.Length - 1)
            : Math.Clamp(_keyboardIndex + amount, 0, apps.Length - 1);
        var selectedRow = _keyboardIndex / layout.Columns;
        _firstRow = Math.Clamp(_firstRow, Math.Max(0, selectedRow - layout.Rows + 1),
            Math.Min(selectedRow, layout.MaximumFirstRow(apps.Length)));
    }

    internal DashboardApp? SelectedApp => _apps is { } apps && _keyboardIndex >= 0 && _keyboardIndex < apps.Length
        ? apps[_keyboardIndex] : null;

    internal void OpenMenu(DashboardLayout layout, int x, int y, DashboardApp app, bool pinned)
    {
        var column = layout.Applications;
        var width = Math.Min(194, column.W - 12);
        const int height = 43;
        var left = Math.Clamp(x, column.X + 6, column.X + column.W - width - 6);
        var top = Math.Clamp(y, column.Y + 6, column.Y + column.H - height - 6);
        _menu = new(new(left, top, width, height), app.Id, pinned);
    }

    internal string? DismissMenu(int x, int y, bool activate)
    {
        var menu = _menu;
        _menu = null;
        return menu is { Pinned: false } && activate && menu.Bounds.Contains(x, y) ? menu.Id : null;
    }

    internal bool IsInWeatherCredit(DashboardLayout layout, int x, int y) =>
        (!layout.Compact || Page == DashboardPage.Weather) && WeatherCredit(layout.Weather).Contains(x, y) &&
        Weather.View.Forecast is not null;

    internal Rect SettingsPanel(DashboardLayout layout, ISettingsPanel settings)
    {
        var viewport = SettingsViewport(layout);
        var height = settings.PanelRect(default, viewport.W, viewport.H).H;
        var scroll = Math.Clamp(_settingsScroll, 0, Math.Max(0, height - viewport.H));
        return new Rect(viewport.X, viewport.Y - scroll, viewport.W, height);
    }

    internal static Rect SettingsViewport(DashboardLayout layout) => layout.Settings;

    internal bool ScrollSettings(DashboardLayout layout, ISettingsPanel settings, int x, int y, int value)
    {
        var viewport = SettingsViewport(layout);
        if ((layout.Compact && Page != DashboardPage.Settings) || !viewport.Contains(x, y))
        {
            return false;
        }
        var panel = SettingsPanel(layout, settings);
        var previous = viewport.Y - panel.Y;
        _settingsScroll = Math.Clamp(previous + value * 4, 0, Math.Max(0, panel.H - viewport.H));
        return previous != _settingsScroll;
    }

    internal void DrawBackdrop(Span<byte> pixels, int width, int height, int stride, int scale, in DamageRect damage)
    {
        var key = (width, height, scale, Background.Version);
        if (_prepared != key)
        {
            var length = checked(width * height * 4);
            if (_backdrop.Length != length)
            {
                _backdrop = GC.AllocateUninitializedArray<byte>(length);
            }
            var full = DamageRect.For(width, height);
            var glass = new FrostedGlass(tinted: false);
            Background.Render(_backdrop, width, height, width * 4);
            glass.Prepare(_backdrop, width, height, width * 4, 18 * scale, full);
            glass.CopyDimmedBlur(_backdrop, opacity: 22);
            glass.LightenBlur(opacity: 36);
            var inset = DashboardLayout.Create(width / scale, height / scale).Glass;
            glass.Draw(_backdrop, width * 4, inset, 26, scale, full, antialiasEdge: true);
            _prepared = key;
        }
        Blitter.Copy(pixels, width, height, stride, _backdrop, width, height, 0, 0, damage);
    }

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, in DamageRect damage,
        ISettingsPanel settings, NotificationStack notifications, (int X, int Y)? pointer)
    {
        DrawBackdrop(pixels, width, height, stride, scale, damage);
        var layout = DashboardLayout.Create(width / scale, height / scale);
        if (!damage.Intersects(layout.Glass.ToDamage(scale)))
        {
            return;
        }
        Weather.Refresh();
        Updates.Refresh();

        var page = Page;
        if (layout.Compact && damage.Intersects(layout.Tabs.ToDamage(scale)))
        {
            foreach (var item in Enum.GetValues<DashboardPage>())
            {
                var tab = layout.Tab(item);
                Chrome.FillRoundedRect(pixels, width, height, stride, tab, 10, item == page ? Selected : Card, scale);
                var name = item == DashboardPage.Applications ? "Apps" : item.ToString();
                var em = tab.W < 95 ? 12 : 14;
                var x = tab.X + Math.Max(4, (tab.W - ShellText.Measure(name, em).Width) / 2);
                Label(pixels, stride, scale, name, x, tab.Y + 9, em, maximum: tab.W - 8);
            }
        }
        var active = Widgets.Active;
        settings = active.Settings ?? settings;
        var frame = new DashboardWidgetFrame(width, height, stride, scale, damage, layout, pointer,
            new(_apps, _firstRow, _keyboardIndex, _menu, _icon), Weather.View, Calendar, [],
            _notificationOffset, settings, SettingsPanel(layout, settings));
        Paint(active, DashboardPage.Applications, layout.Applications, pixels, frame, page);
        Paint(active, DashboardPage.Weather, layout.Weather, pixels, frame, page);
        Paint(active, DashboardPage.Calendar, layout.Calendar, pixels, frame, page);
        if ((!layout.Compact || page == DashboardPage.Notifications) && damage.Intersects(layout.Notifications.ToDamage(scale)))
        {
            var records = notifications.Recent().Concat(Updates.Items).OrderByDescending(item => item.Time).ToArray();
            active[DashboardPage.Notifications].Draw(pixels, frame with { Notifications = records });
        }
        if (settings.IsOpen)
        {
            Paint(active, DashboardPage.Settings, layout.Settings, pixels, frame, page);
        }
    }

    private static void Paint(DashboardWidgetSet active, DashboardPage widget, Rect bounds, Span<byte> pixels,
        in DashboardWidgetFrame frame, DashboardPage page)
    {
        if ((!frame.Layout.Compact || page == widget) && frame.Damage.Intersects(bounds.ToDamage(frame.Scale)))
        {
            active[widget].Draw(pixels, frame);
        }
    }

    internal bool CalendarVisible(DashboardLayout layout) => !layout.Compact || Page == DashboardPage.Calendar;

    internal static Rect CalendarPanel(Rect bounds) => bounds with { H = Math.Min(232, bounds.H) };

    internal int CalendarHover(DashboardLayout layout, (int X, int Y) pointer) =>
        CalendarVisible(layout) ? DashboardCalendar.Hover(CalendarPanel(layout.Calendar), pointer) : 0;

    internal bool PressCalendar(DashboardLayout layout, int x, int y) =>
        CalendarVisible(layout) && Calendar.Click(CalendarPanel(layout.Calendar), x, y);

    internal bool ScrollNotifications(DashboardLayout layout, NotificationStack notifications, int x, int y, int value)
    {
        if ((layout.Compact && Page != DashboardPage.Notifications) || !layout.Notifications.Contains(x, y) || value == 0)
        {
            return false;
        }

        var count = notifications.Recent().Length + Updates.Items.Count;
        var maximum = Math.Max(0, count - VisibleNotifications(layout.Notifications));
        var previous = _notificationOffset;
        _notificationOffset = Math.Clamp(previous + Math.Sign(value), 0, maximum);
        return previous != _notificationOffset;
    }

    internal static int VisibleNotifications(Rect panel) => Math.Max(0, (panel.H - 42) / NotificationRowHeight);

    internal static Rect WeatherCredit(Rect box) => new(box.X + 18, box.Y + 128, Math.Max(0, box.W - 36), 22);

    private IconImage? Icon(string icon, int size)
    {
        if (string.IsNullOrWhiteSpace(icon))
        {
            return null;
        }

        var task = _icons.GetOrAdd((icon, size), key => new Lazy<Task<IconImage?>>(() =>
        {
            var load = Task.Run<IconImage?>(async () =>
            {
                await _iconLoads.WaitAsync().ConfigureAwait(false);
                try
                {
                    var path = IconResolver.Resolve(key.Icon, DesktopLocator.DefaultDataRoots());
                    return path is null ? null : IconLoader.Load(path, key.Size);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    return null;
                }
                finally
                {
                    _iconLoads.Release();
                }
            });
            _ = load.ContinueWith(completed =>
            {
                if (completed.IsCompletedSuccessfully && completed.Result is not null)
                {
                    Notify();
                }
            }, TaskScheduler.Default);
            return load;
        })).Value;
        if (!task.IsCompleted)
        {
            return null;
        }

        return task.IsCompletedSuccessfully ? task.Result : null;
    }

}
