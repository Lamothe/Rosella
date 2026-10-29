using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// The apple system menu: a small rounded panel above the apple button
/// with exactly three items — Log Out, Restart, Shut Down — and no
/// confirmation dialogs. It closes on Esc or a click outside. There is no
/// keyboard navigation, by design.
/// </summary>
internal sealed class SystemMenu
{
    /// <summary>The items, top to bottom.</summary>
    internal static readonly string[] Items = ["Log Out", "Restart", "Shut Down"];

    private const uint PanelBackground = 0xFF2E303A;
    private const uint PanelBorder = 0xFFB8BEC8;
    private const uint TextColour = 0xFFE8E8EC;
    private const uint RowSelected = 0xFFB8BEC8;
    private const uint RowSelectedText = 0xFF101216;

    private const int PanelWidth = 220;
    private const int RowHeight = 40;
    private const int PanelPad = 8;
    private const int CornerRadius = 8;

    private readonly ISystemActions _actions;

    internal SystemMenu(ISystemActions actions) => _actions = actions;

    internal bool IsOpen { get; private set; }

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

    internal void Open() => IsOpen = true;

    internal void Close() => IsOpen = false;

    /// <summary>Handles a key while the menu is open: Esc closes it. True = consumed.</summary>
    internal bool HandleKey(ushort code)
    {
        if (code == InputConstants.KeyEsc)
        {
            Close();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Handles a click. True = consumed (inside the panel). A click on an
    /// item runs its action and closes; a click outside closes without
    /// acting.
    /// </summary>
    internal bool HandleClick(int x, int y, int outputWidth, int outputHeight)
    {
        if (!IsOpen)
        {
            return false;
        }

        var panel = PanelRect(outputWidth, outputHeight);
        if (!panel.Contains(x, y))
        {
            Close();
            return false;
        }

        for (var i = 0; i < Items.Length; i++)
        {
            if (RowRect(panel, i).Contains(x, y))
            {
                Activate(i);
                Close();
                return true;
            }
        }

        return true;
    }

    /// <summary>The panel: above the apple button, clamped to small outputs.</summary>
    internal Rect PanelRect(int outputWidth, int outputHeight)
    {
        var panelWidth = Math.Min(PanelWidth, outputWidth);
        var panelHeight = (PanelPad * 2) + (Items.Length * RowHeight);
        var y = Math.Max(0, outputHeight - Shell.BarHeight - panelHeight);
        return new Rect(Math.Min(Bar.Pad, Math.Max(0, outputWidth - panelWidth)), y, panelWidth, panelHeight);
    }

    /// <summary>One item's row inside the panel.</summary>
    internal static Rect RowRect(Rect panel, int index) =>
        new(panel.X + PanelPad, panel.Y + PanelPad + (index * RowHeight), panel.W - (PanelPad * 2), RowHeight - 4);

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, (int X, int Y)? pointer)
    {
        var panel = PanelRect(width, height);

        // True physical clip bounds (see Bar.Draw): the panel is interior
        // geometry, so only the clip changes with a non-divisible mode.
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;

        // Rounded panel: border colour underneath, background on top, both
        // built from bands and corner discs (see Chrome.FillRoundedRect).
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, panel, CornerRadius, PanelBorder, scale);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(panel.X + 1, panel.Y + 1, panel.W - 2, panel.H - 2), CornerRadius, PanelBackground, scale);

        for (var i = 0; i < Items.Length; i++)
        {
            var row = RowRect(panel, i);
            var hovered = pointer is { } point && row.Contains(point.X, point.Y);
            if (hovered)
            {
                Chrome.FillRoundedRect(pixels, clipW, clipH, stride, row, 6, RowSelected, scale);
            }

            var textColour = hovered ? RowSelectedText : TextColour;
            var textY = row.Y + ((row.H - (BitmapFont.GlyphHeight * Shell.FontScale)) / 2);
            BitmapFont.DrawText(pixels, clipW, clipH, stride, Items[i], (row.X + 12) * scale, textY * scale, textColour, Shell.FontScale * scale);
        }
    }

    private void Activate(int index)
    {
        switch (index)
        {
            case 0:
                _actions.LogOut();
                break;

            case 1:
                _actions.Restart();
                break;

            case 2:
                _actions.ShutDown();
                break;
        }
    }
}
