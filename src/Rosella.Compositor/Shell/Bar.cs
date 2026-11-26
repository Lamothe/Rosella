using System.Globalization;
using System.Text;

#if ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

/// <summary>
/// Floating taskbar with pinned launchers, unpinned windows and a clock.
/// Layout is shared by drawing and input and uses logical pixels.
/// </summary>
internal static class Bar
{
    /// <summary>Distance from the screen edge to the first drawn content.</summary>
    internal const int EdgePad = 8;

    private const int BottomGap = 10;
    private const int SideGap = 12;
    private const int CornerRadius = 16;

    private const int MaxLabelWidth = 196;
    internal const int Pad = 8;

    /// <summary>
    /// Gap between pinned buttons and around their group. The icon sits
    /// inset in its cell as well, so the real spacing between neighbouring
    /// icon bitmaps is this gap plus both paddings.
    /// </summary>
    internal const int PinGap = 8;

    /// <summary>The icon's breathing room inside its cell.</summary>
    internal const int IconPad = 6;

    private const uint BarBackground = 0xFF16171E;
    private const uint BarHairline = 0xFF2A2C36;
    private const uint ClockHoverColour = 0xFF33363F;
    private const uint TaskBackground = 0xFF22242E;
    private const uint TaskFocusedBackground = 0xFF33363F;
    private const uint TaskPressedBackground = 0xFF3D404B;
    private const uint TextColour = 0xFFE8E8EC;

    private static readonly IconImage MutedIconShade = new([0x70000000u], 1, 1);

    /// <summary>The window label shared with the window switcher.</summary>
    internal static string LabelFor(string title, string appId)
    {
        var label = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(appId) ? "Window" : appId.Trim())
            : title.Trim();

        if (ShellText.Measure(label, ShellText.BarEm).Width <= MaxLabelWidth)
        {
            return label;
        }

        var builder = new StringBuilder(label.Length);
        foreach (var character in label)
        {
            var candidate = builder.ToString() + character + "…";
            if (ShellText.Measure(candidate, ShellText.BarEm).Width > MaxLabelWidth)
            {
                break;
            }

            builder.Append(character);
        }

        return builder.Length == 0 ? "…" : $"{builder}…";
    }

    /// <summary>The clock, GNOME style: month, day, then the time.</summary>
    internal static string ClockString() => FormatClock(DateTime.Now);

    /// <summary>The clock's face, split out so tests can pin it.</summary>
    internal static string FormatClock(DateTime moment) =>
        moment.ToString("MMM d H:mm", CultureInfo.InvariantCulture);

    /// <summary>The square side of an icon box: the inner bar height.</summary>
    internal static int IconBoxSize => Shell.BarHeight - BottomGap - 12;

    /// <summary>The square side of an icon: its cell minus the padding.</summary>
    internal static int IconSize => IconBoxSize - (IconPad * 2);

    /// <summary>Where the icon bitmap sits in its cell: inset by the padding on all sides.</summary>
    internal static Rect IconRect(Rect box) => new(box.X + IconPad, box.Y + IconPad, box.W - 2 * IconPad, box.H - 2 * IconPad);

    /// <summary>Packs launchers, unpinned windows and the clock into a centred dock.</summary>
    internal static BarLayout ComputeLayout(int width, int height, string clock, IReadOnlyList<BarTask> tasks, IReadOnlyList<PinnedLauncher>? pinned = null)
    {
        var inset = Math.Min(SideGap, width / 8);
        var maxWidth = width - (2 * inset);
        var padding = Math.Min(EdgePad, maxWidth / 4);
        var clockWidth = Math.Min(maxWidth - (2 * padding),
            ShellText.Measure(clock, ShellText.BarEm).Width + (2 * Pad));
        var available = maxWidth - (2 * padding) - clockWidth;
        var occupied = 0;
        var pins = new List<PinnedButton>();
        var taskButtons = new List<(Rect Rect, BarTask Task)>();
        var innerY = height - Shell.BarHeight + 6;

        pinned ??= [];
        var buttonWidth = IconBoxSize + PinGap;
        var pinCount = Math.Min(pinned.Count, available / buttonWidth);
        if (buttonWidth <= available && tasks.Any(task =>
            !pinned.Take(pinCount).Any(pin => PinnedMatch.Matches(task.AppId, pin.MatchKeys))))
        {
            pinCount = Math.Min(pinCount, (available - buttonWidth) / buttonWidth);
        }

        foreach (var pin in pinned.Take(pinCount))
        {
            var running = tasks.Any(task => PinnedMatch.Matches(task.AppId, pin.MatchKeys));
            pins.Add(new PinnedButton(new Rect(occupied, innerY, IconBoxSize, IconBoxSize), pin, running));
            occupied += buttonWidth;
        }

        foreach (var task in tasks)
        {
            if (pins.Any(pin => PinnedMatch.Matches(task.AppId, pin.Launcher.MatchKeys)))
            {
                continue;
            }

            if (occupied + buttonWidth > available)
            {
                continue;
            }

            taskButtons.Add((new Rect(occupied, innerY, IconBoxSize, IconBoxSize), task));
            occupied += buttonWidth;
        }

        var barWidth = occupied + clockWidth + (2 * padding);
        var bounds = new Rect((width - barWidth) / 2, height - Shell.BarHeight, barWidth, Shell.BarHeight - BottomGap);
        var contentX = bounds.X + padding;
        var clockRect = new Rect(contentX + occupied, innerY, clockWidth, IconBoxSize);
        return new BarLayout(bounds, clockRect,
            pins.Select(pin => pin with { Rect = pin.Rect with { X = pin.Rect.X + contentX } }).ToArray(),
            taskButtons.Select(button => (button.Rect with { X = button.Rect.X + contentX }, button.Task)).ToArray());
    }

    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int scale, BarLayout layout, string clock,
        SurfaceHandler? focused, (int X, int Y)? pointer = null, int pressedPin = -1, FrostedGlass? glass = null, TaskbarIcons? icons = null)
    {
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        var bounds = layout.Bounds;
        if (glass is not null)
        {
            glass.Draw(pixels, stride, bounds, CornerRadius, scale);
        }
        else
        {
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride, bounds, CornerRadius, BarHairline, scale);
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride,
                new Rect(bounds.X + 1, bounds.Y + 1, bounds.W - 2, bounds.H - 2),
                CornerRadius - 1, BarBackground, scale);
        }

        if (pointer is { } pointerPosition && layout.Clock.Contains(pointerPosition.X, pointerPosition.Y))
        {
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride, layout.Clock, 10, ClockHoverColour, scale);
        }

        // The line box reserves room for descenders. Lower the clock's baseline
        // to centre the month and digits while keeping it steady as the text changes.
        const int clockBaselineOffset = 2;
        var clockY = layout.Clock.Y + ((layout.Clock.H - ShellText.LineHeight(ShellText.BarEm)) / 2) + clockBaselineOffset;
        ShellText.DrawText(pixels, clipW, clipH, stride, clock, (layout.Clock.X + Pad) * scale, clockY * scale, TextColour, ShellText.BarEm, scale);

        for (var i = 0; i < layout.Pinned.Length; i++)
        {
            var button = layout.Pinned[i];
            var hovered = pointer is { } point && button.Rect.Contains(point.X, point.Y);

            // The cell's tile appears only under the pointer: at rest the
            // icon floats on the bar, and the padding between icons is
            // plainly visible (the Windows 11 / dock look).
            if (pressedPin == i)
            {
                Chrome.FillRoundedRect(pixels, clipW, clipH, stride, button.Rect, 10, TaskPressedBackground, scale);
            }
            else if (hovered)
            {
                Chrome.FillRoundedRect(pixels, clipW, clipH, stride, button.Rect, 10, TaskFocusedBackground, scale);
            }

            DrawIcon(pixels, clipW, clipH, stride, scale, button);
            if (button.Running)
            {
                DrawRunningIndicator(pixels, clipW, clipH, stride, scale, button.Rect);
            }
        }

        foreach (var (rect, task) in layout.Tasks)
        {
            var isFocused = task.Surface == focused;
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride, rect, 10, isFocused ? TaskFocusedBackground : TaskBackground, scale);
            if (isFocused)
            {
                Chrome.FillRoundedRect(pixels, clipW, clipH, stride,
                    new Rect(rect.X + (rect.W - 16) / 2, rect.Y + rect.H - 4, 16, 3), 1, Chrome.AccentBlue, scale);
            }

            var box = IconRect(rect);
            var scaled = box.Scaled(scale);
            if (icons?.Get(task.AppId, scaled.W, task.Surface.Scene) is { } icon)
            {
                icon.Draw(pixels, clipW, clipH, stride, scaled);
            }
            else
            {
                DrawDefaultWindowIcon(pixels, clipW, clipH, stride, scale, box);
            }

            if (task.Minimized)
            {
                MutedIconShade.Draw(pixels, clipW, clipH, stride, scaled);
            }
        }
    }

    private static void DrawDefaultWindowIcon(Span<byte> pixels, int width, int height, int stride, int scale, Rect box)
    {
        var window = new Rect(box.X + 3, box.Y + 5, box.W - 6, box.H - 10);
        Chrome.FillRoundedRect(pixels, width, height, stride, window, 5, 0xFFAFB6C8, scale);
        Chrome.FillRoundedRect(pixels, width, height, stride,
            new Rect(window.X + 2, window.Y + 6, window.W - 4, window.H - 8), 2, 0xFF414854, scale);
    }

    /// <summary>
    /// A pinned button's face: the decoded icon scaled into the cell's
    /// inset icon square via the ARGB blit, else the fallback — the
    /// Name's first letter drawn in the same inset. The padding around
    /// the icon is the point: icons breathe instead of butting together.
    /// </summary>
    private static void DrawIcon(Span<byte> pixels, int clipW, int clipH, int stride, int scale, PinnedButton button)
    {
        var scaled = IconRect(button.Rect).Scaled(scale);
        if (button.Launcher.Icon is { } icon)
        {
            // Icons are decoded at the output's exact dock size and drawn 1:1.
            icon.Draw(pixels, clipW, clipH, stride, scaled);
            return;
        }

        var letter = button.Launcher.TileLetter.ToString();

        // Like the rest of the bar's text, the fallback letter is drawn at
        // the bar's em: the shell's old doubled scale overflowed the icon
        // inset and struck through the running indicator.
        var (textW, textH) = ShellText.Measure(letter, ShellText.BarEm);
        ShellText.DrawText(pixels, clipW, clipH, stride, letter, scaled.X + ((scaled.W - (textW * scale)) / 2), scaled.Y + ((scaled.H - (textH * scale)) / 2), TextColour, ShellText.BarEm, scale);
    }

    /// <summary>
    /// The running indicator: a 3px accent bar with rounded ends at the
    /// bottom edge of the icon box, shown while the app has a window.
    /// </summary>
    private static void DrawRunningIndicator(Span<byte> pixels, int clipW, int clipH, int stride, int scale, Rect box)
    {
        var barWidth = box.W - 12;
        var x = box.X + 6;
        var y = box.Y + box.H - 3;
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x + 1, y, barWidth - 2, 1), Chrome.AccentBlue, scale);
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x, y + 1, barWidth, 1), Chrome.AccentBlue, scale);
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x + 1, y + 2, barWidth - 2, 1), Chrome.AccentBlue, scale);
    }

}
