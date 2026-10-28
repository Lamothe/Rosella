using System.Globalization;

namespace Rosella.Compositor;

/// <summary>A window button on the taskbar.</summary>
internal sealed record BarTask(string Label, SurfaceHandler Surface);

/// <summary>
/// The taskbar: Windows 11 style — a bottom strip with a Start button, one
/// button per window, and the clock at the right. No workspaces, no
/// configuration. Layout is logical pixels (the input thread hit-tests the
/// same rectangles); drawing scales by the output factor.
/// </summary>
internal static class Bar
{
    private const int StartButtonWidth = 56;
    private const int TaskWidth = 220;
    private const int ClockZoneWidth = 180;
    private const int Pad = 8;

    private const uint BarBackground = 0xFF16171E;
    private const uint BarHairline = 0xFF2A2C36;
    private const uint StartIconColour = 0xFFE8E8EC;
    private const uint TaskBackground = 0xFF22242E;
    private const uint TaskFocusedBackground = 0xFF33363F;
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

    internal static BarLayout ComputeLayout(int width, int height, string clock, IReadOnlyList<BarTask> tasks)
    {
        var innerY = (height - Shell.BarHeight) + 6;
        const int innerH = Shell.BarHeight - 12;

        var start = new Rect(Pad, innerY, StartButtonWidth, innerH);

        var clockWidth = (BitmapFont.Measure(clock, 1).Width + (Pad * 2)) + 8;
        var clockX = Math.Max(0, width - clockWidth - Pad);
        var clockRect = new Rect(clockX, innerY, clockWidth, innerH);

        // Task buttons fill the space between Start and the clock zone.
        var slotStart = Pad + StartButtonWidth + Pad;
        var slotEnd = Math.Max(slotStart, clockX - Pad);
        var maxTasks = Math.Max(0, (slotEnd - slotStart + Pad) / (TaskWidth + Pad));
        var list = new List<(Rect Rect, BarTask Task)>();
        for (var i = 0; i < tasks.Count && i < maxTasks; i++)
        {
            list.Add((new Rect(slotStart + (i * (TaskWidth + Pad)), innerY, TaskWidth, innerH), tasks[i]));
        }

        return new BarLayout(start, clockRect, [.. list]);
    }

    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int scale, BarLayout layout, string clock, SurfaceHandler? focused)
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

    /// <summary>The Start button icon: four small squares, Windows style.</summary>
    private static void DrawStartIcon(Span<byte> pixels, int clipW, int clipH, int stride, int scale, Rect start)
    {
        const int cell = 8;
        const int gap = 3;
        var total = (cell * 2) + gap;
        var x0 = start.X + ((start.W - total) / 2);
        var y0 = start.Y + ((start.H - total) / 2);
        for (var row = 0; row < 2; row++)
        {
            for (var col = 0; col < 2; col++)
            {
                Chrome.FillRect(pixels, clipW, clipH, stride, new Rect(x0 + (col * (cell + gap)), y0 + (row * (cell + gap)), cell, cell), StartIconColour, scale);
            }
        }
    }
}

/// <summary>The drawn taskbar: rectangles and their tasks, in one order.</summary>
internal sealed record BarLayout(Rect Start, Rect Clock, (Rect Rect, BarTask Task)[] Tasks);
