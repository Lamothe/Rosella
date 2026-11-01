using System.Text;

namespace Rosella.Compositor;

/// <summary>
/// The Alt+Tab window switcher: a centred panel listing the windows in
/// most-recently-used order with the selection inverted. The list is
/// snapshotted when the switch opens, so windows mapping or closing
/// mid-switch cannot renumber the rows under the user's fingers; windows
/// destroyed mid-switch are skipped rather than drawn or focused. No
/// thumbnails and no icons, by design: the title and app id are enough to
/// tell windows apart, and rendering client content into an overlay is a
/// different feature.
/// </summary>
internal sealed class TaskSwitcher
{
    private const uint PanelBackground = 0xFF2E303A;
    private const uint PanelBorder = 0xFFB8BEC8;
    private const uint TextColour = 0xFFE8E8EC;
    private const uint RowSelected = 0xFFB8BEC8;
    private const uint RowSelectedText = 0xFF101216;

    /// <summary>The panel's fixed logical width.</summary>
    internal const int PanelWidth = 380;

    /// <summary>The row pitch: the drawn row is shorter, leaving a gap between rows.</summary>
    internal const int RowHeight = 38;

    private const int PanelPad = 8;
    private const int CornerRadius = 8;
    private const int RowRadius = 6;
    private const int RowInset = 12;

    /// <summary>The gap between a row's drawn face and the next row.</summary>
    private const int RowGap = 4;

    /// <summary>The app id's em: smaller than the title, but still readable.</summary>
    private const int AppEm = 12;

    /// <summary>Space between a title and the trailing app id.</summary>
    private const int AppGap = 12;

    /// <summary>Clear space kept above and below the panel, so it never runs to an edge.</summary>
    private const int EdgeMargin = 40;

    /// <summary>The open snapshot in MRU order (newest first), empty when closed.</summary>
    private IReadOnlyList<BarTask> _tasks = [];

    private int _index;

    /// <summary>True while the panel is up and Tab and Esc belong to the switch.</summary>
    internal bool IsOpen { get; private set; }

    /// <summary>The selected snapshot row, or -1 when closed.</summary>
    internal int Index => IsOpen ? _index : -1;

    /// <summary>
    /// Opens over a snapshot of the windows, newest first (the scene lists
    /// bottom-to-top), selecting the previous window so a quick tap toggles
    /// between the two most recent. Opening with no windows is allowed: the
    /// panel simply draws nothing until a window appears.
    /// </summary>
    internal void Open(IReadOnlyList<BarTask> tasks)
    {
        _tasks = [.. NewestFirst(tasks)];
        _index = _tasks.Count > 1 ? 1 : 0;
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

        var workHeight = Math.Max(0, outputHeight - Shell.BarHeight);
        var maxHeight = Math.Max(RowHeight, workHeight - (EdgeMargin * 2));
        var fit = (maxHeight - (PanelPad * 2)) / RowHeight;
        return Math.Clamp(fit, 0, count);
    }

    /// <summary>The panel, centred in the desktop area above the taskbar.</summary>
    internal static Rect PanelRect(int outputWidth, int outputHeight, int rows)
    {
        var width = Math.Min(PanelWidth, Math.Max(0, outputWidth));
        var height = (PanelPad * 2) + (Math.Max(0, rows) * RowHeight);
        var workHeight = Math.Max(0, outputHeight - Shell.BarHeight);
        return new Rect(
            Math.Max(0, (outputWidth - width) / 2),
            Math.Max(0, (workHeight - height) / 2),
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

    /// <summary>
    /// Draws the open switcher over the desktop. Logical pixels scale onto
    /// the physical scanout like the rest of the shell chrome. Rows whose
    /// window was destroyed are skipped, exactly as Next and Commit skip
    /// them, so the highlight always names the window a release would focus.
    /// </summary>
    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, IReadOnlyList<BarTask> tasks)
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

        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, panel, CornerRadius, PanelBorder, scale);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(panel.X + 1, panel.Y + 1, panel.W - 2, panel.H - 2), CornerRadius, PanelBackground, scale);

        for (var slot = 0; slot < visible; slot++)
        {
            var rowIndex = first + slot;
            if (rowIndex >= rows.Count)
            {
                break;
            }

            var task = rows[rowIndex];
            var row = RowRect(panel, slot);
            var isSelected = rowIndex == selected;
            if (isSelected)
            {
                Chrome.FillRoundedRect(pixels, clipW, clipH, stride, row, RowRadius, RowSelected, scale);
            }

            var colour = isSelected ? RowSelectedText : TextColour;

            // The app id gets at most half the row: an unusually long one
            // must not run into the title or spill out of the panel.
            var appId = Fit(task.AppId.Trim(), AppEm, Math.Max(0, (row.W - (RowInset * 2)) / 2));
            var appWidth = appId.Length == 0 ? 0 : ShellText.Measure(appId, AppEm).Width;
            var labelWidth = row.W - (RowInset * 2) - (appWidth == 0 ? 0 : appWidth + AppGap);
            var label = Fit(task.Label, ShellText.MenuEm, Math.Max(0, labelWidth));
            var labelY = row.Y + ((row.H - ShellText.LineHeight(ShellText.MenuEm)) / 2);
            ShellText.DrawText(pixels, clipW, clipH, stride, label, (row.X + RowInset) * scale, labelY * scale, colour, ShellText.MenuEm, scale);

            if (appWidth > 0)
            {
                var appY = row.Y + ((row.H - ShellText.LineHeight(AppEm)) / 2);
                ShellText.DrawText(pixels, clipW, clipH, stride, appId, (row.X + row.W - RowInset - appWidth) * scale, appY * scale, colour, AppEm, scale);
            }
        }
    }

    /// <summary>MRU order: the scene's list runs bottom-to-top, so newest is last.</summary>
    private static IEnumerable<BarTask> NewestFirst(IReadOnlyList<BarTask> tasks) => tasks.Reverse();

    /// <summary>
    /// A window destroyed since the snapshot: the compositor's own focus
    /// rule skips these, so the switcher never hands over a dead surface.
    /// </summary>
    private static bool IsGone(BarTask task) => task.Surface is not { Gone: false };

    /// <summary>
    /// The selected window, skipping forward over rows whose windows died
    /// since the selection landed on them; null when none is left.
    /// </summary>
    private SurfaceHandler? SelectedSurface()
    {
        var count = _tasks.Count;
        for (var step = 0; step < count; step++)
        {
            var task = _tasks[((_index + step) % count + count) % count];
            if (task.Surface is { Gone: false } surface)
            {
                return surface;
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

    /// <summary>
    /// Trims a title with an ellipsis to a measured width, so a long title
    /// cannot run into the trailing app id.
    /// </summary>
    private static string Fit(string text, int em, int width)
    {
        if (ShellText.Measure(text, em).Width <= width)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            var candidate = builder.ToString() + character + "…";
            if (ShellText.Measure(candidate, em).Width > width)
            {
                break;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? "…" : $"{builder}…";
    }
}