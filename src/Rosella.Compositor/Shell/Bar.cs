using System.Globalization;

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
    private const int StartButtonWidth = 56;
    private const int TaskWidth = 220;
    private const int ClockZoneWidth = 180;
    internal const int Pad = 8;

    /// <summary>Gap between pinned buttons and around their group.</summary>
    internal const int PinGap = 4;

    internal const int GroupPad = 8;

    private const uint BarBackground = 0xFF16171E;
    private const uint BarHairline = 0xFF2A2C36;
    private const uint StartIconColour = 0xFFE8E8EC;
    private const uint TaskBackground = 0xFF22242E;
    private const uint TaskFocusedBackground = 0xFF33363F;
    private const uint TaskPressedBackground = 0xFF3D404B;
    private const uint TaskFocusedAccent = 0xFF7B86E8;
    private const uint TextColour = 0xFFE8E8EC;

    /// <summary>The window label: the title, or the app id, trimmed.</summary>
    internal static string LabelFor(string title, string appId)
    {
        var label = string.IsNullOrWhiteSpace(title)
            ? (string.IsNullOrWhiteSpace(appId) ? "Window" : appId.Trim())
            : title.Trim();
        return label.Length <= 24 ? label : $"{label[..23]}…";
    }

    /// <summary>The clock: time first, then the date, Australian style.</summary>
    internal static string ClockString() =>
        DateTime.Now.ToString("HH:mm  dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>The square side of a pinned icon box: the inner bar height.</summary>
    internal static int IconBoxSize => Shell.BarHeight - 12;

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

        var start = new Rect(Pad, innerY, StartButtonWidth, innerH);

        var clockWidth = (BitmapFont.Measure(clock, 1).Width + (Pad * 2)) + 8;
        var clockX = Math.Max(0, width - clockWidth - Pad);
        var clockRect = new Rect(clockX, innerY, clockWidth, innerH);

        // The pinned group: horizontally centred on the bar, one icon box
        // per resolved app with a small gap and side padding. Centring is
        // clamped so the group never overlaps the apple button or the
        // clock: it sits between them when it fits, packed right of the
        // apple as a last resort when it does not.
        pinned ??= [];
        var box = IconBoxSize;
        var groupWidth = GroupWidth(pinned.Count);
        var minGroupX = Pad + StartButtonWidth + Pad;
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
        var slotStart = Pad + StartButtonWidth + Pad;
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
            var tile = pressedPin == i ? TaskPressedBackground : hovered ? TaskFocusedBackground : TaskBackground;
            Chrome.FillRect(pixels, clipW, clipH, stride, button.Rect, tile, scale);
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

            var textY = rect.Y + ((rect.H - BitmapFont.GlyphHeight) / 2);
            BitmapFont.DrawText(pixels, clipW, clipH, stride, task.Label, (rect.X + 12) * scale, textY * scale, TextColour, scale);
        }

        var clockY = layout.Clock.Y + ((layout.Clock.H - BitmapFont.GlyphHeight) / 2);
        BitmapFont.DrawText(pixels, clipW, clipH, stride, clock, (layout.Clock.X + Pad) * scale, clockY * scale, TextColour, scale);
    }

    /// <summary>
    /// A pinned button's face: the decoded icon scaled into the box via
    /// the ARGB blit, else the fallback tile — the Name's first letter on
    /// a panel-button background.
    /// </summary>
    private static void DrawIcon(Span<byte> pixels, int clipW, int clipH, int stride, int scale, PinnedButton button)
    {
        var scaled = button.Rect.Scaled(scale);
        if (button.Launcher.Icon is { } icon)
        {
            // Nearest-neighbour into the box, source-over with premultiplied
            // alpha — exactly the window blit path.
            Blitter.Blit(pixels, clipW, clipH, stride, System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(icon.Pixels), icon.Width, icon.Height, icon.Width * 4, ShmFormats.Argb8888, scaled.X, scaled.Y, scaled.W, scaled.H);
            return;
        }

        var letter = button.Launcher.TileLetter.ToString();
        var (textW, textH) = BitmapFont.Measure(letter, Shell.FontScale);
        BitmapFont.DrawText(pixels, clipW, clipH, stride, letter, scaled.X + ((scaled.W - (textW * scale)) / 2), scaled.Y + ((scaled.H - (textH * scale)) / 2), TextColour, Shell.FontScale * scale);
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
    /// bite (Mac style, per the desktop's opinionated aesthetic), drawn from
    /// a hand-authored pixel-art grid so the shape is deliberate at any
    /// scale rather than an accident of ellipses.
    /// </summary>
    internal static void DrawStartIcon(Span<byte> pixels, int clipW, int clipH, int stride, int scale, Rect start)
    {
        var x0 = start.X + ((start.W - AppleIcon.Width) / 2);
        var y0 = start.Y + ((start.H - AppleIcon.Height) / 2);
        for (var y = 0; y < AppleIcon.Height; y++)
        {
            for (var x = 0; x < AppleIcon.Width; x++)
            {
                if (AppleIcon.IsSet(x, y))
                {
                    Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x0 + x, y0 + y, 1, 1), StartIconColour, scale);
                }
            }
        }
    }
}

/// <summary>
/// The Start button's apple: a hand-authored 20x20 pixel-art silhouette —
/// stem in the top notch, leaf up-right, bite out of the right flank.
/// </summary>
internal static class AppleIcon
{
    internal const int Width = 20;
    internal const int Height = 20;

    private static readonly string[] Rows =
    [
        "........##..........", // 0  stem
        "........##.###......", // 1  stem + leaf
        ".......###..###.....", // 2
        "....#####....###....", // 3  body top + leaf
        "...#############....", // 4
        "..###############...", // 5  shoulder
        ".##############.....", // 6  bite begins
        ".############.......", // 7  bite deepest
        ".############.......", // 8
        ".#############......", // 9  bite ends
        "..##############....", // 10
        "..###############...", // 11
        "..###############...", // 12
        "..###############...", // 13
        "..###############...", // 14
        "...#############....", // 15
        "...#############....", // 16
        "....###########.....", // 17
        ".....####..####.....", // 18 bottom dimple
        "......##....##......", // 19
    ];

    internal static bool IsSet(int x, int y) => Rows[y][x] == '#';
}

/// <summary>The drawn taskbar: rectangles and their tasks, in one order.</summary>
internal sealed record BarLayout(Rect Start, Rect Clock, PinnedButton[] Pinned, (Rect Rect, BarTask Task)[] Tasks);
