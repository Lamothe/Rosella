using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The taskbar: Windows 11 style — bottom strip, Start button, one button
/// per window, clock at the right. The geometry is the same for drawing
/// and hit-testing, and both live in this one layout.
/// </summary>
public class ShellChromeTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    [Fact]
    public void ComputeLayout_PlacesStartBottomLeft()
    {
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", []);

        Assert.Equal(new Rect(8, 1080 - Shell.BarHeight + 6, 56, Shell.BarHeight - 12), layout.Start);
    }

    [Fact]
    public void ComputeLayout_PlacesTheClockAtTheRightEdge()
    {
        var clock = "14:33  28/10/2026";
        var layout = Bar.ComputeLayout(1920, 1080, clock, []);

        Assert.True(layout.Clock.X + layout.Clock.W <= 1920 - 8, "clock overlaps the right edge");
        Assert.True(layout.Clock.W >= BitmapFont.Measure(clock, 1).Width, "clock zone narrower than its text");
    }

    [Fact]
    public void ComputeLayout_TasksFillBetweenStartAndTheClock()
    {
        var scene = new Scene();
        var task = new BarTask("Hello", new SurfaceHandler(scene));
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [task]);

        var (rect, _) = Assert.Single(layout.Tasks);
        Assert.True(rect.X >= layout.Start.X + layout.Start.W, "task overlaps Start");
        Assert.True(rect.X + rect.W <= layout.Clock.X, "task overlaps the clock");
    }

    [Fact]
    public void ComputeLayout_TaskCountIsCappedByTheSpace()
    {
        var scene = new Scene();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => new BarTask($"t{i}", new SurfaceHandler(scene)))
            .ToArray();
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", tasks);

        Assert.True(layout.Tasks.Length > 0 && layout.Tasks.Length < 50, $"got {layout.Tasks.Length} tasks");
    }

    [Fact]
    public void LabelFor_TruncatesLongTitles()
    {
        Assert.Equal("Window", Bar.LabelFor("", ""));
        Assert.Equal("Hello", Bar.LabelFor("Hello", "app"));
        Assert.Equal(24, Bar.LabelFor(new string('x', 40), "app").Length);
    }

    [Fact]
    public void ClockString_IsTimeThenAustralianDate()
    {
        Assert.Matches(@"^\d{2}:\d{2}  \d{2}/\d{2}/\d{4}$", Bar.ClockString());
    }

    [Fact]
    public void Draw_PaintsTheTaskbarAndTheFocusAccent()
    {
        var width = 1920;
        var height = 1080;
        var scene = new Scene();
        var task = new BarTask("Hello", new SurfaceHandler(scene));
        var layout = Bar.ComputeLayout(width, height, Bar.ClockString(), [task]);

        var pixels = new byte[width * height * 4];
        Bar.Draw(pixels, width, height, width * 4, 1, layout, Bar.ClockString(), task.Surface);

        var barY = height - Shell.BarHeight;
        Assert.NotEqual(0u, PixelAt(pixels, width * 4, width / 2, barY + 2));

        // The focused task wears the accent underline at its bottom edge.
        var (rect, _) = layout.Tasks[0];
        Assert.Equal(0xFF7B86E8, PixelAt(pixels, width * 4, rect.X + (rect.W / 2), rect.Y + rect.H - 1));
    }

    [Fact]
    public void StartIcon_IsAnAppleWithABiteAndALeaf()
    {
        var w = 600;
        var h = 48;
        var stride = w * 4;
        var buffer = new byte[stride * h];
        var layout = Bar.ComputeLayout(w, h, "14:33  28/10/2026", []);
        Bar.Draw(buffer, w, h, stride, 1, layout, "14:33  28/10/2026", null);
        var x0 = layout.Start.X + ((layout.Start.W - 20) / 2);
        var y0 = layout.Start.Y + ((layout.Start.H - 20) / 2);

        // Body: solid through the middle and at the lower right, where the
        // silhouette is widest.
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 7, y0 + 13));
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 17, y0 + 13));

        // The bite: the upper-right flank is empty where the body reaches
        // further right below.
        Assert.Equal(0xFF16171Eu, PixelAt(buffer, stride, x0 + 16, y0 + 8));

        // Stem fills the top notch; the leaf sticks out top-right.
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 9, y0 + 2));
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 14, y0 + 1));

        // Everything painted left of the clock zone lives inside the start
        // button's rect (the clock/task labels share the icon's colour).
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < layout.Clock.X - 4; x++)
            {
                if (PixelAt(buffer, stride, x, y) == 0xFFE8E8ECu)
                {
                    Assert.True(layout.Start.Contains(x, y), $"icon pixel outside the start rect at {x},{y}");
                }
            }
        }
    }
}
