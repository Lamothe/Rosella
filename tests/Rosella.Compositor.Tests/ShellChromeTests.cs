using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The floating taskbar: launchers, one button
/// per window, clock at the right. The geometry is the same for drawing
/// and hit-testing, and both live in this one layout.
/// </summary>
public class ShellChromeTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    [Fact]
    public void ComputeLayout_EmptyDockFitsTheClock()
    {
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", []);
        Assert.Equal(layout.Clock.W + (2 * Bar.EdgePad), layout.Bounds.W);
        Assert.Equal((1920 - layout.Bounds.W) / 2, layout.Bounds.X);
    }

    [Fact]
    public void ComputeLayout_PlacesTheClockAtTheRightEdge()
    {
        var clock = "14:33  28/10/2026";
        var layout = Bar.ComputeLayout(1920, 1080, clock, []);

        var bar = layout.Bounds;
        Assert.Equal(bar.X + bar.W - Bar.EdgePad, layout.Clock.X + layout.Clock.W);
        Assert.Equal(ShellText.Measure(clock, ShellText.BarEm).Width + (2 * Bar.Pad), layout.Clock.W);
    }

    [Fact]
    public void ComputeLayout_TasksFillBetweenStartAndTheClock()
    {
        var scene = new Scene();
        var task = new BarTask("Hello", "", new SurfaceHandler(scene));
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [task]);

        var (rect, _) = Assert.Single(layout.Tasks);
        Assert.Equal(Bar.IconBoxSize, rect.W);
        Assert.Equal(rect.W, rect.H);
        Assert.True(rect.X >= layout.Bounds.X + Bar.EdgePad, "task escapes dock padding");
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

        // Truncation is by measured width, not character count: the label
        // must fit the button's inner width whatever the face's metrics.
        var truncated = Bar.LabelFor(new string('x', 40), "app");
        Assert.EndsWith("…", truncated);
        Assert.True(truncated.Length < 40, $"got {truncated.Length} characters");
        Assert.True(
            ShellText.Measure(truncated, ShellText.BarEm).Width <= 220 - 24,
            $"label measures {ShellText.Measure(truncated, ShellText.BarEm).Width}px, wider than the button");

        // A title of capitals is far wider per character than lowercase, so
        // it must be cut shorter — which is the whole point of measuring.
        var capitals = Bar.LabelFor(new string('M', 40), "app");
        Assert.True(capitals.Length <= truncated.Length, $"capitals {capitals.Length} vs lowercase {truncated.Length}");
    }

    [Fact]
    public void FormatClock_IsGnomeStyle_MonthDayThenTime()
    {
        // GNOME's top-bar face, per the desktop's taste: "Oct 30 9:16".
        Assert.Equal("Oct 30 9:16", Bar.FormatClock(new DateTime(2026, 10, 30, 9, 16, 0)));
        Assert.Equal("Jan 2 21:05", Bar.FormatClock(new DateTime(2026, 1, 2, 21, 5, 0)));
    }

    [Fact]
    public void ClockString_MatchesThePinnedFace()
    {
        Assert.Equal(Bar.FormatClock(DateTime.Now), Bar.ClockString());
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

        Assert.NotEqual(0u, PixelAt(pixels, width * 4, width / 2, layout.Bounds.Y + 2));

        // The focused task wears the accent underline at its bottom edge.
        var (rect, _) = layout.Tasks[0];
        Assert.Equal(0xFF3584E4, PixelAt(pixels, width * 4, rect.X + (rect.W / 2), rect.Y + rect.H - 3));
    }

    [Fact]
    public void Draw_UnidentifiedWindowShowsAGenericIconAndDimsWhenMinimized()
    {
        const int width = 600;
        const int height = 100;
        const int stride = width * 4;
        const string clock = "12:34";
        var task = new BarTask("A long title that should never appear in the dock", "", new SurfaceHandler(new Scene()));
        var layout = Bar.ComputeLayout(width, height, clock, [task]);
        var box = Bar.IconRect(Assert.Single(layout.Tasks).Rect);
        var pixels = new byte[stride * height];

        Bar.Draw(pixels, width, height, stride, 1, layout, clock, null);
        var header = PixelAt(pixels, stride, box.X + box.W / 2, box.Y + 6);
        var body = PixelAt(pixels, stride, box.X + box.W / 2, box.Y + box.H / 2);
        Assert.Equal(0xFFAFB6C8u, header);
        Assert.Equal(0xFF414854u, body);

        layout = Bar.ComputeLayout(width, height, clock, [task with { Minimized = true }]);
        Bar.Draw(pixels, width, height, stride, 1, layout, clock, null);
        Assert.InRange(PixelAt(pixels, stride, box.X + box.W / 2, box.Y + 6), 0xFF000001u, header - 1);
    }

    [Theory]
    [InlineData("org.example.RosellaIcon")]
    [InlineData("rosella-icon-class")]
    public void Draw_UsesTheDesktopEntryIconForAnUnpinnedWindow(string appId)
    {
        Assert.True(IconLoader.Available, "gdk-pixbuf is required for the icon rendering test");

        var root = Directory.CreateTempSubdirectory("rosella-taskbar-icons-");
        try
        {
            var applications = root.CreateSubdirectory("applications");
            var png = Path.Combine(AppContext.BaseDirectory, "Fixtures", "icon.png");
            File.WriteAllText(Path.Combine(applications.FullName, "org.example.RosellaIcon.desktop"),
                $"[Desktop Entry]\nName=Icon test\nIcon={png}\nStartupWMClass=rosella-icon-class\n");

            const int width = 640;
            const int height = 100;
            const int scale = 2;
            const int stride = width * scale * 4;
            const string clock = "12:34";
            var scene = new Scene();
            var task = new BarTask("An unpinned window", appId, new SurfaceHandler(scene));
            var layout = Bar.ComputeLayout(width, height, clock, [task]);
            var box = Bar.IconRect(Assert.Single(layout.Tasks).Rect).Scaled(scale);
            var cache = new TaskbarIcons([root.FullName]);
            CompositorSessionTests.WaitFor(() => cache.Get(appId, box.W, scene), icon => icon is not null);

            var pixels = new byte[stride * height * scale];
            Bar.Draw(pixels, width, height, stride, scale, layout, clock, null, icons: cache);
            Assert.Equal(0xFFFF0000u, PixelAt(pixels, stride, box.X + 2, box.Y + 2));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void EmptyDock_DrawsNothingOutsideTheClockAndChrome()
    {
        // An empty dock has no icon or text to the left of the clock.
        var w = 600;
        var h = Shell.BarHeight;
        var stride = w * 4;
        var buffer = new byte[stride * h];
        var layout = Bar.ComputeLayout(w, h, "14:33  28/10/2026", []);
        Bar.Draw(buffer, w, h, stride, 1, layout, "14:33  28/10/2026", null);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < layout.Clock.X - 4; x++)
            {
                var pixel = PixelAt(buffer, stride, x, y);
                Assert.True(
                    pixel is 0u or 0xFF16171Eu or 0xFF2A2C36u,
                    $"ink at {x},{y} where the bar should be bare (0x{pixel:X8})");
            }
        }
    }

    [Fact]
    public void ClockEndsInsideItsButton()
    {
        var w = 600;
        var h = Shell.BarHeight;
        var stride = w * 4;
        var buffer = new byte[stride * h];
        var layout = Bar.ComputeLayout(w, h, "14:33  28/10/2026", []);
        Bar.Draw(buffer, w, h, stride, 1, layout, "14:33  28/10/2026", null);

        // Scan for the rightmost clock glyph, excluding the bar and its border.
        var rightmost = int.MinValue;
        for (var y = 1; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (PixelAt(buffer, stride, x, y) is not (0u or 0xFF16171Eu or 0xFF2A2C36u))
                {
                    rightmost = Math.Max(rightmost, x);
                }
            }
        }

        var textEnd = layout.Clock.X + layout.Clock.W - Bar.Pad;
        Assert.InRange(rightmost, textEnd - 4, textEnd - 1);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ClockTextIsVerticallyCentred(int scale)
    {
        const int width = 600;
        const int height = 100;
        const string clock = "Nov 24 22:27";
        var stride = width * scale * 4;
        var pixels = new byte[stride * height * scale];
        var layout = Bar.ComputeLayout(width, height, clock, []);
        var button = layout.Clock.Scaled(scale);
        Bar.Draw(pixels, width, height, stride, scale, layout, clock, null);

        var inkRows = Enumerable.Range(button.Y, button.H)
            .Where(y => Enumerable.Range(button.X, button.W)
                .Any(x => PixelAt(pixels, stride, x, y) > 0xFF2A2C36u))
            .ToArray();
        var topPadding = inkRows.First() - button.Y;
        var bottomPadding = button.Y + button.H - 1 - inkRows.Last();
        Assert.InRange(topPadding - bottomPadding, -1, 1);
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
    public void ComputeLayout_PacksOnePinnedButtonBesideTheClock()
    {
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [], [Pin("Steam")]);

        var button = Assert.Single(layout.Pinned);
        Assert.Equal(layout.Bounds.X + Bar.EdgePad, button.Rect.X);
        Assert.Equal(button.Rect.X + button.Rect.W + Bar.PinGap, layout.Clock.X);
        Assert.Equal(Bar.IconBoxSize, button.Rect.W);
        Assert.Equal(Bar.IconBoxSize, button.Rect.H);
    }

    [Fact]
    public void ComputeLayout_SixPinnedButtons_HaveGapsAndPaddingAndStayCentred()
    {
        var pins = new[] { "Chrome", "Terminal", "Files", "Code", "Steam", "Resources" }.Select(Pin).ToArray();
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [], pins);

        Assert.Equal(6, layout.Pinned.Length);

        Assert.Equal((1920 - layout.Bounds.W) / 2, layout.Bounds.X);
        Assert.Equal(layout.Bounds.X + Bar.EdgePad, layout.Pinned[0].Rect.X);

        for (var i = 0; i < layout.Pinned.Length; i++)
        {
            Assert.Equal(Bar.IconBoxSize, layout.Pinned[i].Rect.W);
            if (i > 0)
            {
                var gap = layout.Pinned[i].Rect.X - (layout.Pinned[i - 1].Rect.X + layout.Pinned[i - 1].Rect.W);
                Assert.Equal(Bar.PinGap, gap);
            }
        }

        // The final launcher keeps the same gap before the clock.
        var last = layout.Pinned[^1].Rect;
        Assert.Equal(Bar.PinGap, layout.Clock.X - (last.X + last.W));
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
        var height = Shell.BarHeight;
        var stride = width * 4;
        var pins = new[] { Pin("Steam"), Pin("Files") };
        var tasks = new[] { new BarTask("S", "steam", new SurfaceHandler(new Scene())) };
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", tasks, pins);

        var buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null);

        // The running pin wears the 3px accent bar at the bottom edge.
        var box = layout.Pinned[0].Rect;
        Assert.Equal(0xFF3584E4u, PixelAt(buffer, stride, box.X + (box.W / 2), box.Y + box.H - 2));

        // The idle pin wears none: no tile at all, the bar shows through.
        var idle = layout.Pinned[1].Rect;
        Assert.Equal(0xFF16171Eu, PixelAt(buffer, stride, idle.X + (idle.W / 2), idle.Y + idle.H - 2));
    }

    [Fact]
    public void Draw_FallbackTileShowsTheNameFirstLetter()
    {
        var width = 600;
        var height = Shell.BarHeight;
        var stride = width * 4;
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", [], [Pin("Steam")]);

        var buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null);

        // The tile carries antialiased light text inside the icon inset.
        var box = Bar.IconRect(layout.Pinned[0].Rect);
        var letterPixels = 0;
        for (var y = box.Y; y < box.Y + box.H; y++)
        {
            for (var x = box.X; x < box.X + box.W; x++)
            {
                if ((PixelAt(buffer, stride, x, y) & 0xFFu) > 0x80u)
                {
                    letterPixels++;
                }
            }
        }

        Assert.True(letterPixels > 0, "fallback tile drew no letter");
    }

    [Fact]
    public void Draw_PinTileAppearsOnlyOnHoverAndPress()
    {
        var width = 600;
        var height = Shell.BarHeight;
        var stride = width * 4;
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", [], [Pin("Steam")]);
        var box = layout.Pinned[0].Rect;

        var buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null, (box.X + (box.W / 2), box.Y + 2));
        Assert.Equal(0xFF33363Fu, PixelAt(buffer, stride, box.X + (box.W / 2), box.Y + 2)); // hover fill

        buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null, null, pressedPin: 0);
        Assert.Equal(0xFF3D404Bu, PixelAt(buffer, stride, box.X + (box.W / 2), box.Y + 2)); // pressed fill

        buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null);
        Assert.Equal(0xFF16171Eu, PixelAt(buffer, stride, box.X + (box.W / 2), box.Y + 2)); // idle: no tile, the bar shows
    }

    // --- Icon padding: the icons breathe instead of butting together ----

    [Fact]
    public void PinnedIcons_AreInsetInTheirCellsAndThePaddingIsReal()
    {
        // The padding was an explicit design requirement: pin the exact
        // opinionated values so they cannot quietly shrink away again.
        Assert.Equal(6, Bar.IconPad);
        Assert.Equal(8, Bar.PinGap);
        Assert.Equal(30, Bar.IconSize);

        var box = new Rect(100, 200, Bar.IconBoxSize, Bar.IconBoxSize);
        var icon = Bar.IconRect(box);

        Assert.Equal(Bar.IconPad, icon.X - box.X);
        Assert.Equal(Bar.IconPad, icon.Y - box.Y);
        Assert.Equal(Bar.IconPad, (box.X + box.W) - (icon.X + icon.W));
        Assert.Equal(Bar.IconPad, (box.Y + box.H) - (icon.Y + icon.H));
    }

    [Fact]
    public void PinnedIcons_NeighbouringIconsAreSeparatedByTheGapPlusBothPaddings()
    {
        var pins = new[] { "Chrome", "Terminal", "Files", "Code", "Steam", "Resources" }.Select(Pin).ToArray();
        var layout = Bar.ComputeLayout(1920, 1080, "14:33  28/10/2026", [], pins);

        for (var i = 1; i < layout.Pinned.Length; i++)
        {
            var previous = Bar.IconRect(layout.Pinned[i - 1].Rect);
            var icon = Bar.IconRect(layout.Pinned[i].Rect);
            Assert.Equal(Bar.PinGap + (Bar.IconPad * 2), icon.X - (previous.X + previous.W));
        }
    }

    [Fact]
    public void Draw_IconPixelsStayInsideTheIconInset()
    {
        var width = 600;
        var height = Shell.BarHeight;
        var stride = width * 4;

        // A solid opaque-white icon: anything white must land in the
        // inset square, never in the cell's padding.
        var iconPixels = new uint[8 * 8];
        Array.Fill(iconPixels, 0xFFFFFFFFu);
        var pin = new PinnedLauncher("Test", "test.desktop", ["test"], ["test"], null, new IconImage(iconPixels, 8, 8));
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", [], [pin]);

        var buffer = new byte[stride * height];
        Bar.Draw(buffer, width, height, stride, 1, layout, "14:33  28/10/2026", null);

        var box = layout.Pinned[0].Rect;
        var icon = Bar.IconRect(box);
        var insideIcon = 0;
        for (var y = box.Y; y < box.Y + box.H; y++)
        {
            for (var x = box.X; x < box.X + box.W; x++)
            {
                if (PixelAt(buffer, stride, x, y) == 0xFFFFFFFFu)
                {
                    Assert.True(icon.Contains(x, y), $"icon pixel in the cell padding at {x},{y}");
                    insideIcon++;
                }
            }
        }

        Assert.True(insideIcon > 0, "icon drew nothing");
    }

    [Fact]
    public void Draw_FallbackLetterStaysInsideTheIconInset()
    {
        var width = 600;
        var height = Shell.BarHeight;
        var layout = Bar.ComputeLayout(width, height, "14:33  28/10/2026", [], [Pin("Steam")]);

        // Scale 2 is the worst case: the letter must stay in the inset
        // and clear of the running indicator's row.
        var stride = width * 2 * 4;
        var buffer = new byte[stride * height * 2];
        Bar.Draw(buffer, width, height, stride, 2, layout, "14:33  28/10/2026", null);

        var box = layout.Pinned[0].Rect.Scaled(2);
        var inset = Bar.IconRect(layout.Pinned[0].Rect).Scaled(2);
        var seen = 0;
        for (var y = box.Y; y < box.Y + box.H; y++)
        {
            for (var x = box.X; x < box.X + box.W; x++)
            {
                if (PixelAt(buffer, stride, x, y) == 0xFFE8E8ECu)
                {
                    Assert.True(inset.Contains(x, y), $"letter pixel outside the inset at {x},{y}");
                    seen++;
                }
            }
        }

        Assert.True(seen > 0, "fallback letter drew nothing");
    }

    // --- Narrow bars: the group clamps between the slot and the clock ---

    [Fact]
    public void ComputeLayout_NarrowDock_ThePinStaysInsideTheDock()
    {
        var layout = Bar.ComputeLayout(380, 1080, "14:33  28/10/2026", [], [Pin("Steam")]);

        var groupLeft = layout.Pinned[0].Rect.X;
        var groupRight = layout.Pinned[0].Rect.X + layout.Pinned[0].Rect.W;
        Assert.True(groupLeft >= layout.Bounds.X + Bar.EdgePad, "group escapes dock padding");
        Assert.True(groupRight <= layout.Clock.X - Bar.Pad, "group overlaps the clock");
    }

    [Fact]
    public void ComputeLayout_VeryNarrowBar_OmitsPinsThatCannotFit()
    {
        var pins = new[] { Pin("Steam"), Pin("Files"), Pin("Code") };
        var layout = Bar.ComputeLayout(220, 1080, "14:33  28/10/2026", [], pins);

        Assert.True(layout.Pinned.Length < pins.Length);
        Assert.All(layout.Pinned, pin =>
        {
            Assert.True(pin.Rect.X >= layout.Bounds.X + Bar.EdgePad);
            Assert.True(pin.Rect.X + pin.Rect.W <= layout.Clock.X - Bar.Pad);
        });
    }

    // --- Scale-2 drawing: logical chrome mapped onto physical pixels ---

    [Fact]
    public void Draw_IconBlitMapsEverySourcePixelAtScaleTwo()
    {
        var width = 600;
        var height = Shell.BarHeight;
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

        // Sample the icon's inset square, not the cell: the icon occupies
        // the inset, and the padding around it stays bar.
        var iconRect = Bar.IconRect(layout.Pinned[0].Rect).Scaled(scale);
        Assert.Equal(0xFFFF0000u, PixelAt(buffer, stride, iconRect.X + 10, iconRect.Y + 10));
        Assert.Equal(0xFF00FF00u, PixelAt(buffer, stride, iconRect.X + iconRect.W - 10, iconRect.Y + 10));
        Assert.Equal(0xFF0000FFu, PixelAt(buffer, stride, iconRect.X + 10, iconRect.Y + iconRect.H - 10));
        Assert.Equal(0xFFFFFFFFu, PixelAt(buffer, stride, iconRect.X + iconRect.W - 10, iconRect.Y + iconRect.H - 10));

        // Icon pixels never bleed into the cell padding, even at scale 2.
        var box = layout.Pinned[0].Rect.Scaled(scale);
        for (var y = box.Y; y < box.Y + box.H; y++)
        {
            for (var x = box.X; x < box.X + box.W; x++)
            {
                var pixel = PixelAt(buffer, stride, x, y);
                if (pixel is 0xFFFF0000u or 0xFF00FF00u or 0xFF0000FFu or 0xFFFFFFFFu)
                {
                    Assert.True(iconRect.Contains(x, y), $"icon pixel in the cell padding at {x},{y}");
                }
            }
        }
    }

    [Fact]
    public void Draw_RunningIndicatorScalesWithTheOutput()
    {
        var width = 600;
        var height = Shell.BarHeight;
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
        Assert.Equal(0xFF3584E4u, PixelAt(buffer, stride, ((box.X + (box.W / 2)) * 2) + 1, (box.Y + box.H - 2) * 2));
        Assert.Equal(0xFF3584E4u, PixelAt(buffer, stride, ((box.X + (box.W / 2)) * 2) + 1, ((box.Y + box.H - 2) * 2) + 1));

        // The idle pin wears none: the same logical spot stays bar.
        var idle = layout.Pinned[1].Rect;
        Assert.Equal(0xFF16171Eu, PixelAt(buffer, stride, ((idle.X + (idle.W / 2)) * 2) + 1, (idle.Y + idle.H - 2) * 2));
    }
}
