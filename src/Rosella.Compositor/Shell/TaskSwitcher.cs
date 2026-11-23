namespace Rosella.Compositor;

/// <summary>
/// The Alt+Tab window switcher: a centred panel listing the windows in
/// most-recently-used order with application icons and a blue selection. The list is
/// snapshotted when the switch opens, so windows mapping or closing
/// mid-switch cannot renumber the rows under the user's fingers; windows
/// destroyed mid-switch are skipped rather than drawn or focused.
/// </summary>
internal sealed class TaskSwitcher
{
    private const uint RowSelected = 0xFF273B54;
    private const uint RowSelectionBorder = 0xFF4673A9;
    private const uint IconFallback = 0xFF44454D;

    /// <summary>The panel's fixed logical width.</summary>
    internal const int PanelWidth = 440;

    /// <summary>The row pitch: the drawn row is shorter, leaving a gap between rows.</summary>
    internal const int RowHeight = 52;

    private const int PanelPad = 14;
    private const int RowRadius = 12;
    private const int RowInset = 12;
    private const int IconGap = 12;

    /// <summary>The gap between a row's drawn face and the next row.</summary>
    private const int RowGap = 6;

    private const int TitleEm = 15;
    private const int AppEm = 12;

    /// <summary>Clear space kept above and below the panel, so it never runs to an edge.</summary>
    private const int EdgeMargin = 40;

    /// <summary>The open snapshot in MRU order (newest first), empty when closed.</summary>
    private IReadOnlyList<BarTask> _tasks = [];

    private int _index;
    private readonly Func<SurfaceHandler, bool> _isSelectable;
    private readonly Dictionary<(string Id, int Size), Task<AppVisual>> _appVisuals = [];

    internal Action? Dirty { get; set; }

    internal TaskSwitcher(Func<SurfaceHandler, bool>? isSelectable = null)
    {
        _isSelectable = isSelectable ?? IsSelectable;
    }

    internal bool CanSelect(SurfaceHandler surface) => _isSelectable(surface);

    internal static bool IsSelectable(SurfaceHandler surface) =>
        !surface.Gone && surface.IsMapped && surface.XdgRole?.Toplevel is not null;

    /// <summary>True while the panel is up and Tab and Esc belong to the switch.</summary>
    internal bool IsOpen { get; private set; }

    /// <summary>The selected snapshot row, or -1 when closed.</summary>
    internal int Index => IsOpen ? _index : -1;

    /// <summary>
    /// Opens over a snapshot of the windows, newest first (the scene lists
    /// bottom-to-top), with the keyboard-focused window first even if another
    /// window has since mapped above it. Selects the previous window so a
    /// quick tap switches away from the current focus (in the requested direction). Opening with no windows
    /// is allowed: the panel simply draws nothing until a window appears.
    /// </summary>
    internal void Open(IReadOnlyList<BarTask> tasks, SurfaceHandler? focusedWindow = null, bool backwards = false)
    {
        var newestFirst = NewestFirst(tasks).ToList();
        if (focusedWindow is not null)
        {
            var focusedIndex = newestFirst.FindIndex(task => ReferenceEquals(task.Surface, focusedWindow));
            if (focusedIndex > 0)
            {
                var focusedTask = newestFirst[focusedIndex];
                newestFirst.RemoveAt(focusedIndex);
                newestFirst.Insert(0, focusedTask);
            }
        }

        _tasks = newestFirst;
        _index = _tasks.Count > 1 ? (backwards ? _tasks.Count - 1 : 1) : 0;
        IsOpen = true;
    }

    /// <summary>
    /// Steps the selection one window older, or back towards the newest
    /// when <paramref name="backwards"/>. The list is taken again only when
    /// the open snapshot was empty, so windows that appear mid-switch do not
    /// join a switch already underway; a window destroyed mid-switch is
    /// stepped over instead of being selected as a ghost.
    /// </summary>
    internal void Next(bool backwards, IReadOnlyList<BarTask> tasks)
    {
        if (!IsOpen)
        {
            return;
        }

        if (_tasks.Count == 0)
        {
            _tasks = [.. NewestFirst(tasks)];
        }

        var count = _tasks.Count;
        if (count == 0)
        {
            return;
        }

        var step = backwards ? -1 : 1;
        for (var tries = 0; tries < count; tries++)
        {
            _index = ((_index + step) % count + count) % count;
            if (!IsGone(_tasks[_index]))
            {
                return;
            }
        }
    }

    /// <summary>
    /// Ends the switch and hands back the window to focus, or null when
    /// there was none left (an empty list, or every window died). The
    /// snapshot is dropped: the next switch takes a fresh one.
    /// </summary>
    internal SurfaceHandler? Commit()
    {
        var surface = IsOpen ? SelectedSurface() : null;
        Close();
        return surface;
    }

    /// <summary>Closes without focusing: Esc, or the switch abandoned. Selects nothing.</summary>
    internal void Cancel() => Close();

    /// <summary>
    /// How many rows are drawn for a window count on this output: as many
    /// as fit with the edge margin kept clear, or none when the work area
    /// cannot hold one. Rows never shrink to squeeze one more in; the rest
    /// scroll past the selection.
    /// </summary>
    internal static int VisibleRows(int count, int outputHeight)
    {
        if (count <= 0)
        {
            return 0;
        }

        var maxHeight = Math.Min(outputHeight, Math.Max(RowHeight + PanelPad * 2, outputHeight - EdgeMargin * 2));
        var fit = (maxHeight - (PanelPad * 2)) / RowHeight;
        return Math.Clamp(fit, 0, count);
    }

    /// <summary>The panel, centred in the full output.</summary>
    internal static Rect PanelRect(int outputWidth, int outputHeight, int rows)
    {
        var width = Math.Min(PanelWidth, Math.Max(0, outputWidth));
        var height = (PanelPad * 2) + (Math.Max(0, rows) * RowHeight);
        return new Rect(
            Math.Max(0, (outputWidth - width) / 2),
            Math.Max(0, (outputHeight - height) / 2),
            width,
            height);
    }

    /// <summary>
    /// The first row drawn: the selection is kept inside a window of at most
    /// <paramref name="visible"/> rows, scrolling only when it would
    /// otherwise fall outside. The panel therefore never resizes as the
    /// selection moves, however long the list is.
    /// </summary>
    internal static int FirstVisible(int selected, int count, int visible)
    {
        if (count <= 0 || visible <= 0)
        {
            return 0;
        }

        return Math.Clamp(selected - (visible / 2), 0, Math.Max(0, count - visible));
    }

    /// <summary>One row's drawn bounds: the row pitch minus the gap below it.</summary>
    internal static Rect RowRect(Rect panel, int slot) =>
        new(panel.X + PanelPad, panel.Y + PanelPad + (slot * RowHeight), panel.W - (PanelPad * 2), RowHeight - RowGap);

    internal static Rect IconRect(Rect row) =>
        new(row.X + RowInset, row.Y + (row.H - Bar.IconSize) / 2, Bar.IconSize, Bar.IconSize);

    /// <summary>
    /// Draws the open switcher over the desktop. Logical pixels scale onto
    /// the physical scanout like the rest of the shell chrome. Rows whose
    /// window was destroyed are skipped, exactly as Next and Commit skip
    /// them, so the highlight always names the window a release would focus.
    /// </summary>
    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale,
        IReadOnlyList<BarTask> tasks, IReadOnlyList<PinnedLauncher> pinned)
    {
        if (!IsOpen)
        {
            return;
        }

        var snapshot = _tasks;
        if (snapshot.Count == 0)
        {
            return;
        }

        // Labels come from this frame's list, so a retitled window shows its
        // current name; the row order stays frozen at open time.
        var current = new Dictionary<SurfaceHandler, BarTask>(tasks.Count);
        foreach (var task in tasks)
        {
            current[task.Surface] = task;
        }

        var rows = new List<BarTask>(snapshot.Count);
        foreach (var task in snapshot)
        {
            if (IsGone(task))
            {
                continue;
            }

            rows.Add(current.TryGetValue(task.Surface, out var latest) ? latest : task);
        }

        if (rows.Count == 0)
        {
            return;
        }

        var index = Math.Clamp(_index, 0, snapshot.Count - 1);
        var selected = -1;
        var live = 0;
        for (var position = 0; position < snapshot.Count; position++)
        {
            if (IsGone(snapshot[position]))
            {
                continue;
            }

            if (selected < 0 && position >= index)
            {
                selected = live;
            }

            live++;
        }

        if (selected < 0)
        {
            // The selection and everything after it are gone: wrap to the
            // first live row, exactly as Commit does.
            selected = 0;
        }

        var visible = VisibleRows(rows.Count, height);
        if (visible == 0)
        {
            return;
        }

        var first = FirstVisible(selected, rows.Count, visible);
        var panel = PanelRect(width, height, visible);

        // True physical clip bounds (see Bar.Draw): the panel is interior
        // geometry, so only the clip changes with a non-divisible mode.
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;

        Chrome.DrawPanel(pixels, clipW, clipH, stride, panel, scale);

        for (var slot = 0; slot < visible; slot++)
        {
            var rowIndex = first + slot;
            if (rowIndex >= rows.Count)
            {
                break;
            }

            DrawRow(pixels, clipW, clipH, stride, scale, RowRect(panel, slot),
                rows[rowIndex], pinned, rowIndex == selected);
        }
    }

    private void DrawRow(Span<byte> pixels, int width, int height, int stride, int scale,
        Rect row, BarTask task, IReadOnlyList<PinnedLauncher> pinned, bool selected)
    {
        Chrome.FillRoundedRect(pixels, width, height, stride, row, RowRadius,
            selected ? RowSelectionBorder : Chrome.PanelControlBackground, scale);
        if (selected)
        {
            Chrome.FillRoundedRect(pixels, width, height, stride,
                new Rect(row.X + 1, row.Y + 1, row.W - 2, row.H - 2), RowRadius - 1, RowSelected, scale);
        }

        var iconBox = IconRect(row);
        var x = iconBox.X + iconBox.W + IconGap;
        var available = row.X + row.W - RowInset - x;
        if (available < 0)
        {
            return;
        }

        var visual = VisualFor(task.AppId.Trim(), iconBox.W * scale, pinned);
        if (visual.Icon is { } icon)
        {
            icon.Draw(pixels, width, height, stride, iconBox.Scaled(scale));
        }
        else
        {
            Chrome.FillRoundedRect(pixels, width, height, stride, iconBox, 8, IconFallback, scale);
            var name = string.IsNullOrEmpty(visual.Name) ? task.Label : visual.Name;
            var tile = string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name[0]).ToString();
            var (tileWidth, tileHeight) = ShellText.Measure(tile, TitleEm);
            ShellText.DrawText(pixels, width, height, stride, tile,
                (iconBox.X + (iconBox.W - tileWidth) / 2) * scale,
                (iconBox.Y + (iconBox.H - tileHeight) / 2) * scale, Chrome.PanelText, TitleEm, scale);
        }

        var title = task.Surface.XdgRole?.Toplevel?.Title.Trim();
        var label = string.IsNullOrEmpty(title) ? task.Label : title;
        var app = label.Equals(visual.Name, StringComparison.OrdinalIgnoreCase) ? "" : ShellText.Fit(visual.Name, AppEm, available);
        var titleHeight = ShellText.LineHeight(TitleEm);
        var textHeight = titleHeight + (app.Length == 0 ? 0 : ShellText.LineHeight(AppEm));
        var y = row.Y + (row.H - textHeight) / 2;
        ShellText.DrawText(pixels, width, height, stride, ShellText.Fit(label, TitleEm, available), x * scale, y * scale,
            Chrome.PanelText, TitleEm, scale);
        if (app.Length > 0)
        {
            ShellText.DrawText(pixels, width, height, stride, app, x * scale, (y + titleHeight) * scale,
                Chrome.PanelSecondaryText, AppEm, scale);
        }
    }

    private AppVisual VisualFor(string appId, int size, IReadOnlyList<PinnedLauncher> pinned)
    {
        var launcher = pinned.FirstOrDefault(candidate => PinnedMatch.Matches(appId, candidate.MatchKeys));
        if (launcher?.Icon is { } pinnedIcon)
        {
            return new AppVisual(launcher.Name, pinnedIcon);
        }

        var fallback = new AppVisual(launcher?.Name ?? appId[(appId.LastIndexOf('.') + 1)..], null);
        if (appId.Length is 0 or > 256 || appId.Any(character => !char.IsAsciiLetterOrDigit(character)
            && character is not ('.' or '_' or '-')))
        {
            return fallback;
        }

        var key = (appId, size);
        if (!_appVisuals.TryGetValue(key, out var loading))
        {
            // Decode away from scanout. The next frame swaps the fallback
            // tile for the finished bitmap without changing the row.
            if (_appVisuals.Count == 128)
            {
                _appVisuals.Clear();
            }
            loading = Task.Run(() => LoadVisual(appId, size, fallback));
            _appVisuals.Add(key, loading);
            _ = loading.ContinueWith(_ => Dirty?.Invoke(), CancellationToken.None,
                TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        return loading.IsCompletedSuccessfully ? loading.Result : fallback;
    }

    private static AppVisual LoadVisual(string appId, int size, AppVisual fallback)
    {
        var id = appId.EndsWith(".desktop", StringComparison.Ordinal) ? appId : appId + ".desktop";
        var desktop = DesktopLocator.FindDesktopFile([id], DesktopLocator.DefaultSearchDirs());
        var entry = desktop is null ? null : DesktopEntry.ParseFile(desktop);
        var iconPath = IconResolver.Resolve(entry?.Icon ?? appId, DesktopLocator.DefaultDataRoots());
        return new AppVisual(string.IsNullOrWhiteSpace(entry?.Name) ? fallback.Name : entry.Name,
            iconPath is null ? null : IconLoader.Load(iconPath, size));
    }

    private readonly record struct AppVisual(string Name, IconImage? Icon);

    /// <summary>MRU order: the scene's list runs bottom-to-top, so newest is last.</summary>
    private static IEnumerable<BarTask> NewestFirst(IReadOnlyList<BarTask> tasks) => tasks.Reverse();

    /// <summary>
    /// An unmapped, role-less or destroyed snapshot window cannot be focused.
    /// </summary>
    private bool IsGone(BarTask task) => !_isSelectable(task.Surface);

    /// <summary>
    /// The selected window, skipping forward over ineligible rows; null when none is left.
    /// </summary>
    private SurfaceHandler? SelectedSurface()
    {
        var count = _tasks.Count;
        for (var step = 0; step < count; step++)
        {
            var task = _tasks[((_index + step) % count + count) % count];
            if (!IsGone(task))
            {
                return task.Surface;
            }
        }

        return null;
    }

    private void Close()
    {
        IsOpen = false;
        _tasks = [];
        _index = 0;
    }

}
