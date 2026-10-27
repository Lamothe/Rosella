using System.Globalization;

namespace Rosella.Compositor;

/// <summary>One task-strip entry: a mapped toplevel window.</summary>
internal sealed record BarTask(string Label, SurfaceHandler Surface);

/// <summary>
/// The top bar: workspace chips on the left, the clock centred, the task
/// strip right-aligned. The layout is cached per frame (Shell.LastBarLayout)
/// so drawing and hit-testing share one set of rectangles.
/// </summary>
internal static class Bar
{
    private const uint BarBackground = 0xFF24262E;
    private const uint TextColour = 0xFFE8E8EC;
    private const uint DimTextColour = 0xFF8A8F9A;
    private const uint ChipActive = 0xFFB8BEC8;
    private const uint ChipActiveText = 0xFF101216;
    private const uint TaskFocused = 0xFF3A3E4A;

    private const int MaxLabelChars = 20;

    internal static string LabelFor(string title, string appId) =>
        title.Length > 0 ? title : appId.Length > 0 ? appId : "(window)";

    /// <summary>The bar clock, Australian format, invariant weekday names.</summary>
    internal static string ClockString() =>
        DateTime.Now.ToString("ddd dd/MM HH:mm", CultureInfo.InvariantCulture);

    internal static BarLayout ComputeLayout(int outputWidth, string clock, IReadOnlyList<BarTask> tasks)
    {
        var charWidth = BitmapFont.GlyphWidth * Shell.FontScale;
        var chips = new Rect[Shell.WorkspaceCount];
        for (var i = 0; i < chips.Length; i++)
        {
            chips[i] = new Rect(8 + (i * 44), 4, 40, Shell.BarHeight - 8);
        }

        var clockWidth = clock.Length * charWidth;
        var clockRect = new Rect((outputWidth - clockWidth) / 2, (Shell.BarHeight - BitmapFont.GlyphHeight * Shell.FontScale) / 2, clockWidth, BitmapFont.GlyphHeight * Shell.FontScale);

        var taskList = new List<(Rect Rect, BarTask Task)>();
        var x = outputWidth - 8;
        foreach (var task in tasks)
        {
            var label = task.Label.Length > MaxLabelChars ? task.Label[..(MaxLabelChars - 3)] + "..." : task.Label;
            var rect = new Rect(x - (label.Length * charWidth) - 24, 6, (label.Length * charWidth) + 24, Shell.BarHeight - 12);
            if (rect.X < clockRect.X + clockRect.W + 8 || rect.W <= 24)
            {
                break;
            }

            taskList.Add((rect, task));
            x = rect.X;
        }

        taskList.Reverse();
        return new BarLayout(chips, clockRect, taskList.ToArray());
    }

    internal static void Draw(Span<byte> pixels, int width, int height, int stride, BarLayout layout, string clock, int currentWorkspace, SurfaceHandler? focused)
    {
        Chrome.FillRect(pixels, width, height, stride, new Rect(0, 0, width, Shell.BarHeight), BarBackground);

        for (var i = 0; i < layout.WorkspaceChips.Length; i++)
        {
            var chip = layout.WorkspaceChips[i];
            var active = i + 1 == currentWorkspace;
            if (active)
            {
                Chrome.FillRect(pixels, width, height, stride, chip, ChipActive);
            }

            var digit = (i + 1).ToString();
            var digitWidth = digit.Length * BitmapFont.GlyphWidth * Shell.FontScale;
            BitmapFont.DrawText(pixels, width, height, stride, digit, chip.X + ((chip.W - digitWidth) / 2), chip.Y + ((chip.H - BitmapFont.GlyphHeight * Shell.FontScale) / 2), active ? ChipActiveText : DimTextColour, Shell.FontScale);
        }

        BitmapFont.DrawText(pixels, width, height, stride, clock, layout.Clock.X, layout.Clock.Y, TextColour, Shell.FontScale);

        foreach (var (rect, task) in layout.Tasks)
        {
            if (task.Surface == focused)
            {
                Chrome.FillRect(pixels, width, height, stride, rect, TaskFocused);
            }

            var label = task.Label.Length > MaxLabelChars ? task.Label[..(MaxLabelChars - 3)] + "..." : task.Label;
            BitmapFont.DrawText(pixels, width, height, stride, label, rect.X + 12, rect.Y + ((rect.H - BitmapFont.GlyphHeight * Shell.FontScale) / 2), TextColour, Shell.FontScale);
        }
    }
}

/// <summary>The shared draw/hit-test geometry of one bar frame.</summary>
internal sealed record BarLayout(Rect[] WorkspaceChips, Rect Clock, (Rect Rect, BarTask Task)[] Tasks);
