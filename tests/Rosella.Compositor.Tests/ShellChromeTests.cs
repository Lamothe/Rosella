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
        var task = new BarTask("Hello", "", new SurfaceHandler(scene));
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
            .Select(i => new BarTask($"t{i}", "", new SurfaceHandler(scene)))
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
        var task = new BarTask("Hello", "", new SurfaceHandler(scene));
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

        // Body: solid through the middle and at the widest part.
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 9, y0 + 13));
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 2, y0 + 13));

        // The bite: the upper-right flank is empty where the body reaches
        // further right below.
        Assert.Equal(0xFF16171Eu, PixelAt(buffer, stride, x0 + 16, y0 + 8));
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 16, y0 + 13));

        // Stem fills the top notch; the leaf sticks out top-right; the
        // bottom dimple splits the lower lobes.
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 8, y0 + 0));
        Assert.Equal(0xFFE8E8ECu, PixelAt(buffer, stride, x0 + 13, y0 + 1));
        Assert.Equal(0xFF16171Eu, PixelAt(buffer, stride, x0 + 9, y0 + 19));

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

    // --- The pinned launcher group: centring, gaps, padding -------------

    private static PinnedLauncher Pin(string name) =>
        new(name, $"{name.ToLowerInvariant()}.desktop", [name.ToLowerInvariant()], [name.ToLowerInvariant()], null, null);

    [Fact]
    public void ComputeLayout_WithNoResolvedLaunchers_TheGroupIsEmpty()
    {
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [], []);
        Assert.Empty(layout.Pinned);
    }

    [Fact]
    public void ComputeLayout_CentresOnePinnedButton()
    {
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [], [Pin("Steam")]);

        var button = Assert.Single(layout.Pinned);
        var groupWidth = Bar.GroupWidth(1);
        Assert.Equal(Bar.IconBoxSize + (Bar.GroupPad * 2), groupWidth);

        // The group (side padding included) is centred on the whole bar.
        Assert.Equal((1920 - groupWidth) / 2, button.Rect.X - Bar.GroupPad);
        Assert.Equal(Bar.IconBoxSize, button.Rect.W);
        Assert.Equal(Bar.IconBoxSize, button.Rect.H);
    }

    [Fact]
    public void ComputeLayout_SixPinnedButtons_HaveGapsAndPaddingAndStayCentred()
    {
        var pins = new[] { "Chrome", "Terminal", "Files", "Code", "Steam", "Resources" }.Select(Pin).ToArray();
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [], pins);

        Assert.Equal(6, layout.Pinned.Length);

        var groupWidth = Bar.GroupWidth(6);
        Assert.Equal((6 * Bar.IconBoxSize) + (5 * Bar.PinGap) + (Bar.GroupPad * 2), groupWidth);
        Assert.Equal((1920 - groupWidth) / 2, layout.Pinned[0].Rect.X - Bar.GroupPad);

        for (var i = 0; i < layout.Pinned.Length; i++)
        {
            Assert.Equal(Bar.IconBoxSize, layout.Pinned[i].Rect.W);
            if (i > 0)
            {
                var gap = layout.Pinned[i].Rect.X - (layout.Pinned[i - 1].Rect.X + layout.Pinned[i - 1].Rect.W);
                Assert.Equal(Bar.PinGap, gap);
            }
        }

        // The group's right edge mirrors the left padding.
        var last = layout.Pinned[^1].Rect;
        Assert.Equal(Bar.GroupPad, ((1920 - groupWidth) / 2) + groupWidth - (last.X + last.W));
    }

    [Fact]
    public void ComputeLayout_RunningIndicatorFollowsMatchingWindows()
    {
        var scene = new Scene();
        var steam = Pin("Steam");
        var tasks = new[]
        {
            new BarTask("A", "steam", new SurfaceHandler(scene)),
            new BarTask("B", "firefox", new SurfaceHandler(scene)),
        };

        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", tasks, [steam]);

        Assert.True(layout.Pinned[0].Running);

        // The matching window belongs to the pin, not to the task strip.
        var (_, task) = Assert.Single(layout.Tasks);
        Assert.Equal("B", task.Label);
    }

    [Fact]
    public void ComputeLayout_MatchingIsCaseInsensitiveOnAppId()
    {
        var scene = new Scene();
        var tasks = new[] { new BarTask("A", "Steam", new SurfaceHandler(scene)) };

        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", tasks, [Pin("Steam")]);

        Assert.True(layout.Pinned[0].Running);
        Assert.Empty(layout.Tasks);
    }

    [Fact]
    public void Draw_RunningIndicatorIsAnAccentBarAtTheBoxBottom()
    {
        var width = 600;
        var height = 48;
        var stride = width * 4;
        var pins = new[] { Pin("Steam"), Pin("Files") };
        var tasks = new[] { new BarTask("S", "steam", new SurfaceHandler(new Scene())) };
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", tasks, pins);

        var buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null);

        // The running pin wears the 3px accent bar at the bottom edge.
        var box = layout.Pinned[0].Rect;
        Assert.Equal(0xFF7B86E8u, PixelAt(buffer, stride, box.X + (box.W / 2), box.Y + box.H - 2));

        // The idle pin wears none: its bottom edge is plain tile.
        var idle = layout.Pinned[1].Rect;
        Assert.Equal(0xFF22242Eu, PixelAt(buffer, stride, idle.X + (idle.W / 2), idle.Y + idle.H - 2));
    }

    [Fact]
    public void Draw_FallbackTileShowsTheNameFirstLetter()
    {
        var width = 600;
        var height = 48;
        var stride = width * 4;
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", [], [Pin("Steam")]);

        var buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null);

        // The tile carries the letter in the text colour, somewhere in the box.
        var box = layout.Pinned[0].Rect;
        var letterPixels = 0;
        for (var y = box.Y; y < box.Y + box.H; y++)
        {
            for (var x = box.X; x < box.X + box.W; x++)
            {
                if (PixelAt(buffer, stride, x, y) == 0xFFE8E8ECu)
                {
                    letterPixels++;
                }
            }
        }

        Assert.True(letterPixels > 0, "fallback tile drew no letter");
    }

    [Fact]
    public void Draw_HoverAndPressChangeThePinTile()
    {
        var width = 600;
        var height = 48;
        var stride = width * 4;
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", [], [Pin("Steam")]);
        var box = layout.Pinned[0].Rect;

        var buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null, (box.X + 1, box.Y + 1));
        Assert.Equal(0xFF33363Fu, PixelAt(buffer, stride, box.X + 1, box.Y + 1)); // hover fill

        buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null, null, pressedPin: 0);
        Assert.Equal(0xFF3D404Bu, PixelAt(buffer, stride, box.X + 1, box.Y + 1)); // pressed fill

        buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null);
        Assert.Equal(0xFF22242Eu, PixelAt(buffer, stride, box.X + 1, box.Y + 1)); // idle fill
    }

    // --- Narrow bars: the group clamps between the apple and the clock --

    [Fact]
    public void ComputeLayout_NarrowBar_TheGroupStaysBetweenTheAppleAndTheClock()
    {
        var layout = Bar.ComputeLayout(380, 1080, "14:33  28/10/2026", [], [Pin("Steam")]);

        var groupLeft = layout.Pinned[0].Rect.X - Bar.GroupPad;
        var groupRight = layout.Pinned[0].Rect.X + layout.Pinned[0].Rect.W + Bar.GroupPad;
        Assert.True(groupLeft >= layout.Start.X + layout.Start.W + Bar.Pad, "group overlaps the apple button");
        Assert.True(groupRight <= layout.Clock.X - Bar.Pad, "group overlaps the clock");
    }

    [Fact]
    public void ComputeLayout_VeryNarrowBar_TheGroupPacksRightOfTheApple()
    {
        // Three pins cannot fit between the apple and the clock at 220px:
        // the group left-packs as a last resort and still never overlaps
        // the apple button.
        var pins = new[] { Pin("Steam"), Pin("Files"), Pin("Code") };
        var layout = Bar.ComputeLayout(220, 1080, "14:33  28/10/2026", [], pins);

        var groupLeft = layout.Pinned[0].Rect.X - Bar.GroupPad;
        Assert.True(groupLeft >= layout.Start.X + layout.Start.W + Bar.Pad, "group overlaps the apple button");
    }

    // --- Scale-2 drawing: logical chrome mapped onto physical pixels ---

    [Fact]
    public void Draw_IconBlitMapsEverySourcePixelAtScaleTwo()
    {
        var width = 600;
        var height = 48;
        var scale = 2;

        // A 2x2 icon, one colour per source pixel: the nearest-neighbour
        // blit must map each source pixel onto its quadrant of the box
        // when the output scale is 2.
        var icon = new IconImage([0xFFFF0000u, 0xFF00FF00u, 0xFF0000FFu, 0xFFFFFFFFu], 2, 2);
        var pin = new PinnedLauncher("Steam", "steam.desktop", ["steam"], ["steam"], "/tmp/steam.png", icon);
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", [], [pin]);

        var stride = width * scale * 4;
        var buffer = new byte[stride * height * scale];
        Bar.Draw(buffer, width, height, stride, scale, layout, "14:33  28/10/2026", null);

        var box = layout.Pinned[0].Rect.Scaled(scale);
        Assert.Equal(0xFFFF0000u, PixelAt(buffer, stride, box.X + 10, box.Y + 10));
        Assert.Equal(0xFF00FF00u, PixelAt(buffer, stride, box.X + box.W - 10, box.Y + 10));
        Assert.Equal(0xFF0000FFu, PixelAt(buffer, stride, box.X + 10, box.Y + box.H - 10));
        Assert.Equal(0xFFFFFFFFu, PixelAt(buffer, stride, box.X + box.W - 10, box.Y + box.H - 10));
    }

    [Fact]
    public void Draw_RunningIndicatorScalesWithTheOutput()
    {
        var width = 600;
        var height = 48;
        var scale = 2;
        var pins = new[] { Pin("Steam"), Pin("Files") };
        var tasks = new[] { new BarTask("S", "steam", new SurfaceHandler(new Scene())) };
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", tasks, pins);

        var stride = width * scale * 4;
        var buffer = new byte[stride * height * scale];
        Bar.Draw(buffer, width, height, stride, scale, layout, "14:33  28/10/2026", null);

        // The running pin's accent bar covers both physical rows of its
        // middle logical row at scale 2.
        var box = layout.Pinned[0].Rect;
        Assert.Equal(0xFF7B86E8u, PixelAt(buffer, stride, ((box.X + (box.W / 2)) * 2) + 1, (box.Y + box.H - 2) * 2));
        Assert.Equal(0xFF7B86E8u, PixelAt(buffer, stride, ((box.X + (box.W / 2)) * 2) + 1, ((box.Y + box.H - 2) * 2) + 1));

        // The idle pin wears none: the same logical spot stays plain tile.
        var idle = layout.Pinned[1].Rect;
        Assert.Equal(0xFF22242Eu, PixelAt(buffer, stride, ((idle.X + (idle.W / 2)) * 2) + 1, (idle.Y + idle.H - 2) * 2));
    }
}
