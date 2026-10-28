using System.Diagnostics;

namespace Rosella.Compositor;

/// <summary>
/// The app launcher: Super opens it, arrows choose, Enter launches, Esc
/// closes. The app list is fixed; there is no configuration.
/// </summary>
internal sealed class Launcher
{
    internal static readonly (string Name, string Command)[] Apps =
    [
        ("Files", "nautilus"),
        ("Web Browser", "firefox"),
        ("Text Editor", "gnome-text-editor"),
        ("System Monitor", "gnome-system-monitor"),
    ];

    private const uint PanelBackground = 0xFF2E303A;
    private const uint PanelBorder = 0xFFB8BEC8;
    private const uint TextColour = 0xFFE8E8EC;
    private const uint RowSelected = 0xFFB8BEC8;
    private const uint RowSelectedText = 0xFF101216;

    private const int PanelWidth = 560;
    private const int RowHeight = 40;

    internal bool IsOpen { get; private set; }

    internal int Selection { get; private set; }

    /// <summary>The socket name launched apps should connect to.</summary>
    internal string? SocketName { get; set; }

    /// <summary>Test seam: (command, socketName) -> started.</summary>
    internal Func<string, string?, bool>? SpawnOverride { get; set; }

    internal void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    internal void Open()
    {
        IsOpen = true;
        Selection = 0;
    }

    internal void Close()
    {
        IsOpen = false;
    }

    /// <summary>Handles a key while the launcher is open. True = consumed.</summary>
    internal bool HandleKey(ushort code)
    {
        switch (code)
        {
            case Rosella.Input.InputConstants.KeyUp:
                Selection = Math.Max(0, Selection - 1);
                return true;

            case Rosella.Input.InputConstants.KeyDown:
                Selection = Math.Min(Apps.Length - 1, Selection + 1);
                return true;

            case Rosella.Input.InputConstants.KeyEnter:
                Launch(Apps[Selection].Command);
                Close();
                return true;

            case Rosella.Input.InputConstants.KeyEsc:
                Close();
                return true;

            default:
                return false;
        }
    }

    /// <summary>
    /// Handles a click while the launcher is open. True = consumed (inside
    /// the panel); a click on a row launches that app and closes.
    /// </summary>
    internal bool HandleClick(int x, int y, int outputWidth, int outputHeight)
    {
        var panel = PanelRect(outputWidth, outputHeight);
        if (!panel.Contains(x, y))
        {
            return false;
        }

        for (var i = 0; i < Apps.Length; i++)
        {
            if (RowRect(panel, i).Contains(x, y))
            {
                Selection = i;
                Launch(Apps[i].Command);
                Close();
                return true;
            }
        }

        return true;
    }

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale)
    {
        var panel = PanelRect(width, height);

        // True physical clip bounds (see Bar.Draw): the panel is interior
        // geometry, so only the clip changes with a non-divisible mode.
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        Chrome.FillRect(pixels, clipW, clipH, stride, panel, PanelBackground, scale);
        Chrome.FrameRect(pixels, clipW, clipH, stride, panel, PanelBorder, scale);
        BitmapFont.DrawText(pixels, clipW, clipH, stride, "Pinned", (panel.X + 16) * scale, (panel.Y + 12) * scale, TextColour, Shell.FontScale * scale);

        for (var i = 0; i < Apps.Length; i++)
        {
            var row = RowRect(panel, i);
            var selected = i == Selection;
            if (selected)
            {
                Chrome.FillRect(pixels, clipW, clipH, stride, row, RowSelected, scale);
            }

            BitmapFont.DrawText(pixels, clipW, clipH, stride, Apps[i].Name, (row.X + 12) * scale, (row.Y + ((row.H - BitmapFont.GlyphHeight * Shell.FontScale) / 2)) * scale, selected ? RowSelectedText : TextColour, Shell.FontScale * scale);
        }
    }

    internal Rect PanelRect(int outputWidth, int outputHeight)
    {
        // Windows 11 style: centred horizontally, resting on the taskbar,
        // clamped to fit small outputs.
        var panelWidth = Math.Min(PanelWidth, outputWidth);
        var panelHeight = 56 + (Apps.Length * RowHeight) + 8;
        var y = outputHeight - Shell.BarHeight - panelHeight;
        return new Rect(Math.Max(0, (outputWidth - panelWidth) / 2), Math.Max(0, y), panelWidth, panelHeight);
    }

    private static Rect RowRect(Rect panel, int index) =>
        new(panel.X + 8, panel.Y + 48 + (index * RowHeight), panel.W - 16, RowHeight - 4);

    private void Launch(string command)
    {
        if (SpawnOverride is { } spawn)
        {
            spawn(command, SocketName);
            return;
        }

        try
        {
            var process = Process.Start(CreateStartInfo(command, SocketName));
            Console.WriteLine($"spawn: {command} pid {process?.Id.ToString() ?? "none"}");
            process?.Dispose();
        }
        catch (Exception exception)
        {
            // Opinionated: a missing app simply does not open.
            Console.WriteLine($"spawn: {command} failed: {exception.Message}");
        }
    }

    /// <summary>
    /// The spawn environment for a launched app: its Wayland socket and dark
    /// mode, always (GTK3 reads GTK_THEME, GTK4/libadwaita reads the debug
    /// colour-scheme override). There is no setting.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string command, string? socket = null)
    {
        var startInfo = new ProcessStartInfo { FileName = command, UseShellExecute = false };
        if (socket is not null)
        {
            startInfo.Environment["WAYLAND_DISPLAY"] = socket;
        }

        startInfo.Environment["GTK_THEME"] = "Adwaita:dark";
        startInfo.Environment["ADW_DEBUG_COLOR_SCHEME"] = "prefer-dark";
        return startInfo;
    }
}
