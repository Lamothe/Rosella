namespace Rosella.Compositor;

/// <summary>
/// The floating dock, quick settings and window switcher.
/// </summary>
internal sealed class Shell
{
    /// <summary>Taskbar height in logical pixels.</summary>
    internal const int BarHeight = 64;
    private const int RevealEdge = 2;
    private static readonly object PinnedGate = new();

    private volatile bool _dockRevealed;
    private volatile PinnedLauncher[] _pinned = [];
    private volatile WeakReference<SurfaceHandler>[] _taskOrder = [];
    private readonly ISystemActions _systemActions;
    private readonly IVolumeControl _volume;
    private readonly string? _taskbarPath;
    private TaskbarModuleControls? _moduleControls;
    private volatile TaskbarGeneration _taskbar;
    private volatile string? _lastReload;

    internal bool DockVisible => _dockRevealed || Dashboard.IsOpen || Settings.IsOpen || DockMenu is not null || TaskbarPress is not null;

    internal volatile DockMenu? DockMenu;
    internal volatile DockPress? TaskbarPress;

    /// <summary>Reveal at the bottom edge, with room to move up into the dock.</summary>
    internal bool UpdateDockReveal(int pointerY, int outputHeight)
    {
        var reveal = pointerY >= outputHeight - (DockVisible ? BarHeight : RevealEdge);
        if (reveal == _dockRevealed)
        {
            return false;
        }

        _dockRevealed = reveal;
        return true;
    }

    /// <summary>Menu text scale (the bitmap font's natural size is 8x16).</summary>
    internal const int FontScale = 2;

    internal Shell(ISystemActions? systemActions = null, IVolumeControl? volumeControl = null, TaskSwitcher? switcher = null,
        LaunchSpinner? spinner = null, string? taskbarPath = null)
    {
        _systemActions = systemActions ?? new SystemActions(() => { });
        _volume = volumeControl ?? new WpctlVolumeControl();
        _taskbarPath = taskbarPath;
        var builtIn = new TaskbarImplementation();
        _taskbar = new TaskbarGeneration(builtIn, builtIn.CreateSettings(_systemActions, _volume), null);
        if (_taskbarPath is not null)
        {
            _moduleControls = new TaskbarModuleControls(() => (_taskbar.BuildLabel, _lastReload), ReloadFromSettings);
        }
        _taskbar.Settings.Modules = _moduleControls;
        if (_taskbarPath is not null && File.Exists(_taskbarPath))
        {
            try
            {
                ReloadTaskbar();
                _lastReload = null;
            }
            catch (InvalidOperationException)
            {
                // The built-in taskbar stays available if the module fails at login.
            }
        }
        Switcher = switcher ?? new TaskSwitcher();
        Spinner = spinner ?? new LaunchSpinner();
    }

    /// <summary>The clock-anchored system settings and power panel.</summary>
    internal ISettingsPanel Settings => Dashboard.Widgets.Active.Settings ?? _taskbar.Settings;
    internal TaskbarGeneration Taskbar => _taskbar;
    internal bool CanReloadTaskbar => _taskbarPath is not null;

    internal string DescribeModules()
    {
        var build = _taskbar.BuildId ?? "built-in";
        var installed = "unavailable";
        if (_taskbarPath is not null)
        {
            try
            {
                installed = TaskbarGeneration.InstalledBuild(_taskbarPath);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                installed = error.Message;
            }
        }
        var status = $"taskbar  Taskbar & Quick Settings\n  display: {SocketName ?? "unknown"}\n  active: {build}\n" +
            $"  installed: {installed}\n" +
            $"  module file: {_taskbarPath ?? "unavailable"}\n" +
            $"  last reload: {_lastReload ?? "none"}";
        return Dashboard.Widgets.Available ? status + "\n" + Dashboard.Widgets.Describe() : status;
    }

    internal void ConfigureDashboardWidgets(string path)
    {
        var widgets = Dashboard.Widgets;
        var choices = new List<ModuleReloadChoice>();
        if (_moduleControls is { } taskbar)
        {
            choices.Add(taskbar.Selected with { Label = "Taskbar" });
        }
        foreach (var (page, _, label) in DashboardWidgetModules.Entries)
        {
            choices.Add(new ModuleReloadChoice(label, () => widgets.ReadStatus(page),
                () => ReloadFromSettings(() => ReloadDashboardWidgets(page))));
        }
        _moduleControls = new TaskbarModuleControls(choices);
        Settings.Modules = _moduleControls;
        widgets.Configure(path, _systemActions, _volume, PrepareDashboardSettings);
        if (File.Exists(path))
        {
            ReloadFromSettings(() => ReloadDashboardWidgets(null));
        }
    }

    private void PrepareDashboardSettings(ISettingsPanel next)
    {
        var previous = Settings;
        next.Resolution = previous.Resolution;
        next.Modules = _moduleControls;
        if (previous.ModulesOpen)
        {
            next.OpenModules();
        }
        else if (previous.IsOpen)
        {
            next.Toggle();
        }
        next.Dirty = previous.Dirty;
        previous.Dirty = null;
        previous.Close();
    }

    internal string ReloadDashboardWidgets(DashboardPage? page)
    {
        try
        {
            var result = Dashboard.Widgets.Reload(page);
            Console.WriteLine($"rosella: {SocketName ?? "startup"}: {result}");
            return result;
        }
        catch (InvalidOperationException error)
        {
            Console.Error.WriteLine($"rosella: {error.Message}");
            throw;
        }
        finally
        {
            Settings.Dirty?.Invoke();
        }
    }

    // Called before input starts, or under the input router's gate. Paint
    // takes one generation for layout and drawing without blocking input.
    internal string ReloadTaskbar()
    {
        lock (PinnedGate)
        {
            return ReloadTaskbarCore();
        }
    }

    private string ReloadTaskbarCore()
    {
        if (_taskbarPath is null)
        {
            throw new InvalidOperationException("Taskbar reload is unavailable in this build");
        }

        PinnedLauncher[] pinned;
        TaskbarGeneration next;
        try
        {
            pinned = ResolvePinned?.Invoke() ?? Pinned;
            next = TaskbarGeneration.Load(_taskbarPath, _systemActions, _volume);
        }
        catch (Exception error)
        {
            var message = $"Could not load taskbar module: {error.GetBaseException().Message}";
            _lastReload = "Reload failed · active build kept";
            Settings.Dirty?.Invoke();
            Console.Error.WriteLine($"rosella: {message}");
            throw new InvalidOperationException(message, error);
        }

        var previous = _taskbar;
        var reopenModules = previous.Settings.ModulesOpen;
        previous.Settings.Close();
        next.Settings.Dirty = previous.Settings.Dirty;
        previous.Settings.Dirty = null;
        next.Settings.Resolution = previous.Settings.Resolution;
        next.Settings.Modules = _moduleControls;
        DockMenu = null;
        TaskbarPress = null;
        Pinned = pinned;
        _taskbar = next;
        _lastReload = next.BuildId == previous.BuildId ? "Same build reloaded" : "New build loaded";
        if (reopenModules)
        {
            next.Settings.OpenModules();
        }
        else if (Dashboard.IsOpen && Dashboard.Widgets.Active.Settings is null)
        {
            next.Settings.Toggle();
        }
        previous.Context?.Unload();
        Settings.Dirty?.Invoke();
        var result = $"{_lastReload} · {next.BuildLabel}";
        Console.WriteLine($"rosella: taskbar for {SocketName ?? "startup"}: {result}");
        return result;
    }

    private void ReloadFromSettings() => ReloadFromSettings(ReloadTaskbar);

    private static void ReloadFromSettings(Func<string> reload)
    {
        try
        {
            reload();
        }
        catch (InvalidOperationException)
        {
            // The Modules page displays the result while the active build stays loaded.
        }
    }

    internal FrostedGlass DockGlass { get; } = new();
    internal FrostedGlass SettingsGlass { get; } = new(tinted: false);
    internal Dashboard Dashboard { get; init; } = new();

    internal ScreenshotSelection Screenshot { get; } = new();
    internal NotificationStack Notifications { get; } = new();

    /// <summary>
    /// The Alt+Tab window switcher: a centred overlay drawn over the whole
    /// desktop, above the taskbar and the settings panel.
    /// </summary>
    internal TaskSwitcher Switcher { get; }

    /// <summary>
    /// The launch spinner: an animated pointer shown from
    /// the click that spawns an app until its window appears. The scene
    /// wires its clock, live-app check and dirty callback.
    /// </summary>
    internal LaunchSpinner Spinner { get; }

    internal StartupApps? StartupApps { get; set; }

    internal Func<PinnedLauncher[]>? ResolvePinned { get; init; }
    internal Func<string, string?> FindPinDesktopId { get; init; } =
        appId => PinnedApps.DesktopIdForWindow(appId, DesktopLocator.DefaultSearchDirs());
    internal Func<string, PinnedLauncher?>? ResolvePin { get; init; }
    internal Action<string>? SavePinRemoval { get; init; }
    internal Action<string>? SavePinAddition { get; init; }
    internal Action<string, string, bool>? SavePinMove { get; init; }

    /// <summary>The pinned taskbar launchers resolved at startup or on reload.</summary>
    internal PinnedLauncher[] Pinned { get => _pinned; set => _pinned = value; }

    internal void RemovePin(string desktopId, Shell? other)
    {
        lock (PinnedGate)
        {
            SavePinRemoval?.Invoke(desktopId);
            ForgetPin(desktopId);
            other?.ForgetPin(desktopId);
        }
    }

    private void ForgetPin(string desktopId)
    {
        Pinned = Pinned.Where(pin => pin.DesktopId != desktopId).ToArray();
    }

    internal void AddPin(string desktopId, Shell? other)
    {
        lock (PinnedGate)
        {
            if (Pinned.Any(pin => pin.DesktopId == desktopId))
            {
                return;
            }
            var launcher = ResolvePin?.Invoke(desktopId)
                ?? throw new InvalidOperationException("the app's launcher is no longer available");
            var remote = other?.ResolvePin?.Invoke(desktopId);
            SavePinAddition?.Invoke(desktopId);
            Pinned = [.. Pinned, launcher];
            if (other is not null && remote is not null && !other.Pinned.Any(pin => pin.DesktopId == desktopId))
            {
                other.Pinned = [.. other.Pinned, remote];
            }
        }
    }

    internal void MovePin(string desktopId, string anchorId, bool after, Shell? other)
    {
        lock (PinnedGate)
        {
            if (MoveRelative(Pinned, pin => pin.DesktopId == desktopId, pin => pin.DesktopId == anchorId, after) is not { } order)
            {
                return;
            }
            SavePinMove?.Invoke(desktopId, anchorId, after);
            Pinned = order;
            if (other is not null &&
                MoveRelative(other.Pinned, pin => pin.DesktopId == desktopId, pin => pin.DesktopId == anchorId, after) is { } remote)
            {
                other.Pinned = remote;
            }
        }
    }

    internal IReadOnlyList<BarTask> OrderTasks(IReadOnlyList<BarTask> tasks)
    {
        var order = _taskOrder;
        if (order.Length == 0 || tasks.Count < 2)
        {
            return tasks;
        }
        return tasks.OrderBy(task =>
        {
            var position = Array.FindIndex(order, reference => reference.TryGetTarget(out var surface) && surface == task.Surface);
            return position < 0 ? int.MaxValue : position;
        }).ToArray();
    }

    internal void MoveTask(IReadOnlyList<BarTask> tasks, SurfaceHandler window, SurfaceHandler anchor, bool after)
    {
        if (MoveRelative(OrderTasks(tasks).ToArray(), task => task.Surface == window, task => task.Surface == anchor, after) is { } order)
        {
            _taskOrder = order.Select(task => new WeakReference<SurfaceHandler>(task.Surface)).ToArray();
        }
    }

    private static T[]? MoveRelative<T>(T[] items, Predicate<T> source, Predicate<T> anchor, bool after)
    {
        var from = Array.FindIndex(items, source);
        var to = Array.FindIndex(items, anchor);
        if (from < 0 || to < 0 || from == to)
        {
            return null;
        }
        var insertion = to - (from < to ? 1 : 0) + (after ? 1 : 0);
        if (insertion == from)
        {
            return null;
        }
        var result = items.ToList();
        result.RemoveAt(from);
        result.Insert(insertion, items[from]);
        return result.ToArray();
    }

    /// <summary>The socket name launched apps should connect to.</summary>
    internal string? SocketName { get; set; }

    /// <summary>The taskbar's window registry (focus-or-launch); wired by the entry point.</summary>
    internal IWindowRegistry? Windows { get; set; }

    /// <summary>
    /// The taskbar layout the scanout drew last frame: the input thread
    /// hit-tests exactly this, so drawn and clicked rectangles never
    /// disagree.
    /// </summary>
    internal volatile BarLayout? LastBarLayout;

    internal BarLayout ComputeBarLayout(int width, int height, IReadOnlyList<BarTask> tasks)
    {
        var module = _taskbar.Module;
        return module.ComputeLayout(width, height, module.ClockString(), OrderTasks(tasks), Pinned);
    }

    internal DockLabel? HoverLabel(BarLayout layout, (int X, int Y)? pointer, int width) =>
        DockVisible && DockMenu is null && TaskbarPress is null && !Switcher.IsOpen &&
        pointer is { } point && layout.HoveredIcon(point) is { } icon
            ? DockLabel.Create(icon.Anchor, icon.Name, width) : null;

    /// <summary>
    /// Draws the taskbar and, when open, the settings panel. Width and
    /// height are logical pixels; the chrome is drawn scaled onto the
    /// physical scanout. The pointer drives hover highlighting.
    /// </summary>
    internal void Draw(TaskbarGeneration taskbar, Span<byte> pixels, int width, int height, int stride, int scale, string clock,
        IReadOnlyList<BarTask> tasks, BarLayout layout, bool dockVisible, Rect? settingsPanel, SurfaceHandler? focused,
        in DamageRect damage, (int X, int Y)? pointer)
    {
        if (dockVisible && DockGlass.HasDamage)
        {
            var press = TaskbarPress;
            taskbar.Module.Draw(pixels, width, height, stride, scale, layout, clock, focused, pointer, press?.PinIndex(layout) ?? -1, DockGlass);
            if (press is { Dragging: true } && pointer is { } point && press.DropAt(layout, point) is { } drop)
            {
                Chrome.FillRoundedRect(pixels, stride / 4, pixels.Length / stride, stride, drop.Marker, 1, Chrome.AccentBlue, scale);
            }
        }

        if (settingsPanel is { } panel && SettingsGlass.HasDamage)
        {
            Settings.Draw(pixels, stride, scale, panel, pointer, SettingsGlass);
        }

        DockMenu?.Draw(pixels, stride, scale, pointer);
        HoverLabel(layout, pointer, width)?.Draw(pixels, stride, scale, damage);

        if (Switcher.IsOpen)
        {
            Switcher.Draw(pixels, width, height, stride, scale, tasks, Pinned);
        }

        if (ReferenceEquals(taskbar, _taskbar))
        {
            LastBarLayout = layout;
        }
    }
}

/// <summary>The actions available for an app icon, anchored above the dock.</summary>
internal sealed record DockMenu(Rect Bounds, SurfaceHandler[] CloseWindows, SurfaceHandler? MoveWindow, string? StartupId,
    bool StartsAtLogin, string? RemovePinId, string? AddPinId)
{
    private const int RowHeight = 36;
    internal bool ReloadTaskbar { get; init; }

    internal static DockMenu CreateTaskbarMenu(Rect clock, int outputWidth) =>
        Create(clock, outputWidth, []) with { ReloadTaskbar = true };

    internal static DockMenu Create(Rect anchor, int outputWidth, SurfaceHandler[] windows, SurfaceHandler? moveWindow = null,
        string? startupId = null, bool startsAtLogin = false, string? removePinId = null, string? addPinId = null)
    {
        var width = Math.Min(startupId is not null ? 200 : moveWindow is not null || removePinId is not null || addPinId is not null ? 180 : 144, outputWidth);
        var rows = (moveWindow is null ? 0 : 1) + (windows.Length == 0 ? 0 : 1) +
            (startupId is null ? 0 : 1) + (removePinId is null && addPinId is null ? 0 : 1);
        var height = RowHeight * Math.Max(1, rows);
        var x = Math.Clamp(anchor.X + (anchor.W - width) / 2, 0, outputWidth - width);
        return new DockMenu(new Rect(x, Math.Max(0, anchor.Y - height - 14), width, height), windows, moveWindow, startupId,
            startsAtLogin, removePinId, addPinId);
    }

    internal bool HitMove(int x, int y) => MoveWindow is not null && Row(0).Contains(x, y);

    internal bool HitClose(int x, int y) => CloseWindows.Length > 0 && Row(MoveWindow is null ? 0 : 1).Contains(x, y);

    private int StartupRow => (MoveWindow is null ? 0 : 1) + (CloseWindows.Length == 0 ? 0 : 1);

    internal bool HitStartup(int x, int y) => StartupId is not null && Row(StartupRow).Contains(x, y);

    private Rect PinRow => Row(StartupRow + (StartupId is null ? 0 : 1));

    internal bool HitRemovePin(int x, int y) => RemovePinId is not null && PinRow.Contains(x, y);

    internal bool HitAddPin(int x, int y) => AddPinId is not null && PinRow.Contains(x, y);

    internal Rect HoverBounds((int X, int Y) pointer) => Bounds.Contains(pointer.X, pointer.Y)
        ? Row((pointer.Y - Bounds.Y) / RowHeight) : default;

    private Rect Row(int index) => new(Bounds.X, Bounds.Y + index * RowHeight, Bounds.W, RowHeight);

    internal void Draw(Span<byte> pixels, int stride, int scale, (int X, int Y)? pointer)
    {
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, Bounds, 10, 0xFF4B4D56, scale);
        var inset = new Rect(Bounds.X + 1, Bounds.Y + 1, Bounds.W - 2, Bounds.H - 2);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, inset, 9, 0xFF23242A, scale);
        if (ReloadTaskbar)
        {
            DrawRow(pixels, stride, scale, Row(0), "Reload Taskbar", pointer);
        }
        if (MoveWindow is not null)
        {
            DrawRow(pixels, stride, scale, Row(0), "Move to This Screen", pointer);
        }
        if (CloseWindows.Length > 0)
        {
            DrawRow(pixels, stride, scale, Row(MoveWindow is null ? 0 : 1),
                CloseWindows.Length == 1 ? "Close Window" : "Close All Windows", pointer);
        }
        if (StartupId is not null)
        {
            var row = Row(StartupRow);
            DrawRow(pixels, stride, scale, row, "Start at Login", pointer);
            if (StartsAtLogin)
            {
                PowerIcons.Draw(pixels, clipW, clipH, stride, PowerGlyph.Check,
                    row.X + row.W - 16, row.Y + row.H / 2, 0xFF7BB7FF, scale, 16);
            }
        }
        if (RemovePinId is not null)
        {
            DrawRow(pixels, stride, scale, PinRow, "Remove from Taskbar", pointer);
        }
        else if (AddPinId is not null)
        {
            DrawRow(pixels, stride, scale, PinRow, "Pin to taskbar", pointer);
        }
    }

    private static void DrawRow(Span<byte> pixels, int stride, int scale, Rect row, string label, (int X, int Y)? pointer)
    {
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        if (pointer is { } point && row.Contains(point.X, point.Y))
        {
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride,
                new Rect(row.X + 3, row.Y + 3, row.W - 6, row.H - 6), 7, 0xFF41434C, scale);
        }
        var y = ShellText.InkCenteredY(label, ShellText.BarEm, row, scale);
        ShellText.DrawText(pixels, clipW, clipH, stride, label, (row.X + 12) * scale, y, 0xFFF4F4F5, ShellText.BarEm, scale);
    }
}
