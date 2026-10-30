using System.Globalization;
using System.Text;

namespace Rosella.Compositor;

/// <summary>A window button on the taskbar.</summary>
internal sealed record BarTask(string Label, string AppId, SurfaceHandler Surface);

/// <summary>One pinned launcher button on the taskbar.</summary>
internal sealed record PinnedButton(Rect Rect, PinnedLauncher Launcher, bool Running);

/// <summary>
/// The taskbar: Windows 11 style — a bottom strip with the apple button at
/// the left, the pinned app launchers as one centred group, one button per
/// remaining window, and the clock at the right. No workspaces, no
/// configuration. Layout is logical pixels (the input thread hit-tests the
/// same rectangles); drawing scales by the output factor.
/// </summary>
internal static class Bar
{
    /// <summary>Distance from the screen edge to the first drawn content.</summary>
    internal const int EdgePad = 8;

    private const int TaskWidth = 220;

    /// <summary>The gap between a task button's edge and its label.</summary>
    private const int LabelInset = 12;
    internal const int Pad = 8;

    /// <summary>
    /// Gap between pinned buttons and around their group. The icon sits
    /// inset in its cell as well, so the real spacing between neighbouring
    /// icon bitmaps is this gap plus both paddings.
    /// </summary>
    internal const int PinGap = 8;

    internal const int GroupPad = 8;

    /// <summary>The icon's breathing room inside its cell.</summary>
    internal const int IconPad = 6;

    private const uint BarBackground = 0xFF16171E;
    private const uint BarHairline = 0xFF2A2C36;
    private const uint StartIconColour = 0xFFE8E8EC;
    private const uint TaskBackground = 0xFF22242E;
    private const uint TaskFocusedBackground = 0xFF33363F;
    private const uint TaskPressedBackground = 0xFF3D404B;
    private const uint TaskFocusedAccent = 0xFF7B86E8;
    private const uint TextColour = 0xFFE8E8EC;

    /// <summary>The window label: the title, or the app id, trimmed to the button.</summary>
    internal static string LabelFor(string title, string appId)
    {
        var label = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(appId) ? "Window" : appId.Trim())
            : title.Trim();

        // Truncate by measured width, not by character count: the old 24
        // characters was sized for the fixed 8×16 cell, and a proportional
        // face can make 24 capitals wider than the whole button. The label
        // has the button minus its two 12px insets to breathe in.
        const int AvailableWidth = TaskWidth - (LabelInset * 2);
        if (ShellText.Measure(label, ShellText.BarEm).Width <= AvailableWidth)
        {
            return label;
        }

        var builder = new StringBuilder(label.Length);
        foreach (var character in label)
        {
            var candidate = builder.ToString() + character + "…";
            if (ShellText.Measure(candidate, ShellText.BarEm).Width > AvailableWidth)
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

    /// <summary>The square side of a pinned icon box: the inner bar height.</summary>
    internal static int IconBoxSize => Shell.BarHeight - 12;

    /// <summary>The square side of a pinned icon: its cell minus the padding.</summary>
    internal static int IconSize => IconBoxSize - (IconPad * 2);

    /// <summary>Where the icon bitmap sits in its cell: inset by the padding on all sides.</summary>
    internal static Rect IconRect(Rect box) => new(box.X + IconPad, box.Y + IconPad, IconSize, IconSize);

    /// <summary>The pinned group's extent for a button count (side padding and gaps).</summary>
    internal static int GroupWidth(int count) =>
        count <= 0 ? 0 : (count * IconBoxSize) + ((count - 1) * PinGap) + (GroupPad * 2);

    /// <summary>
    /// The one layout both drawing and hit-testing use. The pinned group is
    /// centred on the whole bar (clamped to sit between the apple button
    /// and the clock); task buttons fill between it and the clock, and only
    /// for windows no pinned launcher claims.
    /// </summary>
    internal static BarLayout ComputeLayout(int width, int height, string clock, IReadOnlyList<BarTask> tasks, IReadOnlyList<PinnedLauncher>? pinned = null)
    {
        var innerY = (height - Shell.BarHeight) + 6;
        var innerH = IconBoxSize;

        // The apple's bitmap lands EdgePad from the screen edge: the button
        // is the icon's own box, hugging the edge, so the corner stays
        // quiet and the logo sits close to it.
        var start = new Rect(0, innerY, IconBoxSize, innerH);

        var clockWidth = ShellText.Measure(clock, ShellText.BarEm).Width + Pad + EdgePad;
        var clockX = Math.Max(0, width - clockWidth);
        var clockRect = new Rect(clockX, innerY, clockWidth, innerH);

        // The pinned group: horizontally centred on the bar, one icon box
        // per resolved app with a small gap and side padding. Centring is
        // clamped so the group never overlaps the apple button or the
        // clock: it sits between them when it fits, packed right of the
        // apple as a last resort when it does not.
        pinned ??= [];
        var box = IconBoxSize;
        var groupWidth = GroupWidth(pinned.Count);
        var minGroupX = start.X + start.W + Pad;
        var maxGroupX = Math.Max(minGroupX, clockX - Pad - groupWidth);
        var groupX = Math.Clamp((width - groupWidth) / 2, minGroupX, maxGroupX);
        var pins = new PinnedButton[pinned.Count];
        for (var i = 0; i < pinned.Count; i++)
        {
            var rect = new Rect(groupX + GroupPad + (i * (box + PinGap)), innerY, box, box);
            var running = tasks.Any(task => PinnedMatch.Matches(task.AppId, pinned[i].MatchKeys));
            pins[i] = new PinnedButton(rect, pinned[i], running);
        }

        // Task buttons fill the space between the pinned group and the
        // clock — windows the pinned launchers do not already claim.
        var slotStart = start.X + start.W + Pad;
        if (pinned.Count > 0)
        {
            slotStart = Math.Max(slotStart, groupX + groupWidth + Pad);
        }

        var slotEnd = Math.Max(slotStart, clockX - Pad);
        var maxTasks = Math.Max(0, (slotEnd - slotStart + Pad) / (TaskWidth + Pad));
        var remaining = tasks.Where(task => pinned.All(launcher => !PinnedMatch.Matches(task.AppId, launcher.MatchKeys))).ToList();
        var list = new List<(Rect Rect, BarTask Task)>();
        for (var i = 0; i < remaining.Count && i < maxTasks; i++)
        {
            list.Add((new Rect(slotStart + (i * (TaskWidth + Pad)), innerY, TaskWidth, innerH), remaining[i]));
        }

        return new BarLayout(start, clockRect, pins, [.. list]);
    }

    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int scale, BarLayout layout, string clock, SurfaceHandler? focused, (int X, int Y)? pointer = null, int pressedPin = -1)
    {
        // Chrome primitives take logical rects and the output scale. The
        // clip bounds are the true physical buffer extent (stride and row
        // count), so a mode not divisible by the scale still gets full
        // bleed — full-width rects round up to cover the remainder.
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        var fullW = (clipW + scale - 1) / scale;

        // Chrome primitives take logical rects and the output scale.
        var barY = height - Shell.BarHeight;

        // + scale on the height overshoots the bottom edge on purpose:
        // clipped to clipH, so a non-divisible mode has no un-drawn strip.
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(0, barY, fullW, Shell.BarHeight + scale), BarBackground, scale);
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(0, barY, fullW, 1), BarHairline, scale);

        DrawStartIcon(pixels, clipW, clipH, stride, scale, layout.Start);

        for (var i = 0; i < layout.Pinned.Length; i++)
        {
            var button = layout.Pinned[i];
            var hovered = pointer is { } point && button.Rect.Contains(point.X, point.Y);

            // The cell's tile appears only under the pointer: at rest the
            // icon floats on the bar, and the padding between icons is
            // plainly visible (the Windows 11 / dock look).
            if (pressedPin == i)
            {
                Chrome.FillRect(pixels, clipW, clipH, stride, button.Rect, TaskPressedBackground, scale);
            }
            else if (hovered)
            {
                Chrome.FillRect(pixels, clipW, clipH, stride, button.Rect, TaskFocusedBackground, scale);
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
            Chrome.FillRect(pixels, clipW, clipH, stride, rect, isFocused ? TaskFocusedBackground : TaskBackground, scale);
            if (isFocused)
            {
                Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(rect.X, rect.Y + rect.H - 2, rect.W, 2), TaskFocusedAccent, scale);
            }

            var textY = rect.Y + ((rect.H - ShellText.LineHeight(ShellText.BarEm)) / 2);
            ShellText.DrawText(pixels, clipW, clipH, stride, task.Label, (rect.X + 12) * scale, textY * scale, TextColour, ShellText.BarEm, scale);
        }

        var clockY = layout.Clock.Y + ((layout.Clock.H - ShellText.LineHeight(ShellText.BarEm)) / 2);
        ShellText.DrawText(pixels, clipW, clipH, stride, clock, (layout.Clock.X + Pad) * scale, clockY * scale, TextColour, ShellText.BarEm, scale);
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
            // Nearest-neighbour into the box, source-over with premultiplied
            // alpha — exactly the window blit path.
            Blitter.Blit(pixels, clipW, clipH, stride, System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(icon.Pixels), icon.Width, icon.Height, icon.Width * 4, ShmFormats.Argb8888, scaled.X, scaled.Y, scaled.W, scaled.H);
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
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x + 1, y, barWidth - 2, 1), TaskFocusedAccent, scale);
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x, y + 1, barWidth, 1), TaskFocusedAccent, scale);
        Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x + 1, y + 2, barWidth - 2, 1), TaskFocusedAccent, scale);
    }

    /// <summary>
    /// The Start button icon: an apple silhouette with a stem, a leaf and a
    /// bite (Mac style, per the desktop's opinionated aesthetic), drawn
    /// anti-aliased at the output scale from the flattened SVG path.
    /// </summary>
    internal static void DrawStartIcon(Span<byte> pixels, int clipW, int clipH, int stride, int scale, Rect start)
    {
        var x0 = start.X + ((start.W - AppleIcon.Width) / 2);
        var y0 = start.Y + ((start.H - AppleIcon.Height) / 2);
        AppleIcon.Blend(pixels, clipW, clipH, stride, x0, y0, scale, StartIconColour);
    }
}

/// <summary>The drawn taskbar: rectangles and their tasks, in one order.</summary>
internal sealed record BarLayout(Rect Start, Rect Clock, PinnedButton[] Pinned, (Rect Rect, BarTask Task)[] Tasks);
