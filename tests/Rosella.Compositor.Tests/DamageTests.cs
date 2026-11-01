using System.Buffers.Binary;
using System.Net.Sockets;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Damage tracking and the fast paths that make a repaint cheap: the model
/// itself, the pixel-for-pixel equivalence of a damaged frame against a full
/// one, and the counted cost of each.
/// </summary>
public class DamageTests
{
    // ---------------------------------------------------------------- model

    [Fact]
    public void Tracker_KeepsDisjointRectsSeparate()
    {
        var tracker = new DamageTracker();
        tracker.Add(new DamageRect(10, 10, 20, 20));
        tracker.Add(new DamageRect(300, 400, 20, 20));

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = tracker.Take(1000, 1000, damage);

        // The pointer and the clock live in opposite corners: one bounding
        // rect would be nearly the whole screen, so they must stay apart.
        Assert.Equal(2, count);
        Assert.Equal(new DamageRect(10, 10, 20, 20), damage[0]);
        Assert.Equal(new DamageRect(300, 400, 20, 20), damage[1]);
    }

    [Fact]
    public void Tracker_MergesTouchingRects()
    {
        var tracker = new DamageTracker();
        tracker.Add(new DamageRect(10, 10, 20, 20));
        tracker.Add(new DamageRect(20, 20, 20, 20));

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = tracker.Take(1000, 1000, damage);

        Assert.Equal(1, count);
        Assert.Equal(new DamageRect(10, 10, 30, 30), damage[0]);
    }

    [Fact]
    public void Tracker_FullDamageIsOneWholeOutputRect()
    {
        var tracker = new DamageTracker();
        tracker.Add(new DamageRect(1, 2, 3, 4));
        tracker.MarkFull();

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = tracker.Take(800, 600, damage);

        Assert.Equal(1, count);
        Assert.Equal(new DamageRect(0, 0, 800, 600), damage[0]);
        Assert.False(tracker.IsFull);
    }

    [Fact]
    public void Tracker_ClipsRectsToTheOutput()
    {
        var tracker = new DamageTracker();
        tracker.Add(new DamageRect(-10, -10, 30, 30)); // straddles the origin
        tracker.Add(new DamageRect(90, 90, 30, 30)); // straddles the far corner

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = tracker.Take(100, 100, damage);

        Assert.Equal(2, count);
        Assert.Equal(new DamageRect(0, 0, 20, 20), damage[0]);
        Assert.Equal(new DamageRect(90, 90, 10, 10), damage[1]);
    }

    [Fact]
    public void Tracker_ReportsNothingForAnEmptyFrame()
    {
        var tracker = new DamageTracker();
        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];

        Assert.Equal(0, tracker.Take(100, 100, damage));

        // A wake with nothing to paint is not a frame: this is what stops the
        // clock bound turning into a repaint per second.
        tracker.Add(DamageRect.Empty);
        Assert.Equal(0, tracker.Take(100, 100, damage));
    }

    [Fact]
    public void Tracker_CollapsesPiecesThatNearlyTileTheirBox()
    {
        var tracker = new DamageTracker();
        tracker.Add(new DamageRect(0, 0, 50, 100));
        tracker.Add(new DamageRect(50, 0, 50, 100));

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = tracker.Take(100, 100, damage);

        Assert.Equal(1, count);
        Assert.Equal(new DamageRect(0, 0, 100, 100), damage[0]);
    }

    [Fact]
    public void Tracker_StaysBoundedWhenDamageFragments()
    {
        var tracker = new DamageTracker();
        for (var index = 0; index < DamageTracker.Capacity; index++)
        {
            tracker.Add(new DamageRect(index * 100, 0, 10, 10));
        }

        Assert.Equal(DamageTracker.Capacity, tracker.Count);

        // One more than the list can carry collapses to its bounding box:
        // a frame must never make the list grow without bound.
        tracker.Add(new DamageRect(0, 500, 10, 10));
        Assert.Equal(1, tracker.Count);
    }

    [Fact]
    public void RectList_SubtractsAHoleIntoPieces()
    {
        var list = new RectList();
        list.Reset(new DamageRect(0, 0, 100, 100));
        list.Subtract(new DamageRect(40, 40, 20, 20));

        Assert.False(list.Overflowed);
        Assert.Equal(4, list.Count);
        Assert.Equal((100 * 100) - (20 * 20), list.Area);
    }

    [Fact]
    public void RectList_OverflowIsReportedNotIgnored()
    {
        var list = new RectList();
        list.Reset(new DamageRect(0, 0, 1000, 1000));
        for (var index = 0; index < 20; index++)
        {
            list.Subtract(new DamageRect(index * 40, index * 40, 10, 10));
        }

        // The bound is a promise to the render loop: overflow means "paint
        // everything", never a silently missing piece.
        Assert.True(list.Overflowed);
    }

    // ----------------------------------------------------------- background

    [Fact]
    public void Background_RegionMatchesFullRender()
    {
        var full = new byte[64 * 48 * 4];
        Background.Render(full, 64, 48, 64 * 4);

        var region = new byte[64 * 48 * 4];
        Array.Fill(region, (byte)0xCC);
        var written = Background.RenderRegion(region, 64, 48, 64 * 4, new DamageRect(10, 20, 30, 8));

        // The gradient is a function of the row, so a region keeps its place
        // in the fade rather than restarting it at the region's top.
        Assert.Equal(30 * 8, written);
        for (var y = 0; y < 48; y++)
        {
            for (var x = 0; x < 64; x++)
            {
                var inside = x >= 10 && x < 40 && y >= 20 && y < 28;
                var offset = ((y * 64) + x) * 4;
                if (inside)
                {
                    Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(full.AsSpan(offset)), BinaryPrimitives.ReadUInt32LittleEndian(region.AsSpan(offset)));
                }
                else
                {
                    Assert.Equal(0xCC, region[offset]);
                }
            }
        }
    }

    // -------------------------------------------------------------- blitter

    /// <summary>The sampler's formula, written out per pixel — the behaviour fast paths must reproduce.</summary>
    private static uint Reference(uint[] source, int sourceWidth, int sourceHeight, uint format, int destinationX, int destinationY, int destinationWidth, int destinationHeight, uint target)
    {
        var sourceColumn = (int)(((long)destinationX * sourceWidth) / destinationWidth);
        var sourceRow = (int)(((long)destinationY * sourceHeight) / destinationHeight);
        var colour = source[(sourceRow * sourceWidth) + sourceColumn];
        if (format == ShmFormats.Xrgb8888)
        {
            return colour;
        }

        var alpha = colour >> 24;
        if (alpha == 0)
        {
            return target;
        }

        if (alpha == 255)
        {
            return colour;
        }

        var inverse = 255 - alpha;
        var red = ((colour >> 16) & 0xFF) + ((((target >> 16) & 0xFF) * inverse) / 255);
        var green = ((colour >> 8) & 0xFF) + ((((target >> 8) & 0xFF) * inverse) / 255);
        var blue = (colour & 0xFF) + (((target & 0xFF) * inverse) / 255);
        return 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    private static uint[] MixedAlphaColours(int count)
    {
        var colours = new uint[count];
        for (var index = 0; index < count; index++)
        {
            var alpha = (uint)((index * 61) % 256);
            colours[index] = (alpha << 24) | (uint)(index * 977 & 0xFFFFFF);
        }

        return colours;
    }

    private static void AssertMatchesReference(uint[] sourceColours, int sourceWidth, int sourceHeight, uint format, int destinationWidth, int destinationHeight, int targetWidth, int targetHeight, uint background)
    {
        var source = new byte[sourceWidth * sourceHeight * 4];
        sourceColours.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.AsSpan()));
        var target = new byte[targetWidth * targetHeight * 4];
        var targetPixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target.AsSpan());
        targetPixels.Fill(background);

        Blitter.Blit(target, targetWidth, targetHeight, targetWidth * 4, source, sourceWidth, sourceHeight, sourceWidth * 4, format, 0, 0, destinationWidth, destinationHeight);

        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                var expected = Reference(sourceColours, sourceWidth, sourceHeight, format, x, y, destinationWidth, destinationHeight, background);
                Assert.True(expected == targetPixels[(y * targetWidth) + x], $"pixel {x},{y}: expected {expected:X8}, got {targetPixels[(y * targetWidth) + x]:X8}");
            }
        }
    }

    [Theory]
    [InlineData(ShmFormats.Argb8888)]
    [InlineData(ShmFormats.Xrgb8888)]
    public void Blit_Identity_MatchesTheReferenceFormula(uint format)
    {
        // source and destination the same size: no sampling at all — the
        // buffer-scale-2 client on a scale-2 output, which is the hot case.
        AssertMatchesReference(MixedAlphaColours(12), 4, 3, format, 4, 3, 4, 3, 0xFF203040);
    }

    [Theory]
    [InlineData(ShmFormats.Argb8888)]
    [InlineData(ShmFormats.Xrgb8888)]
    public void Blit_IntegerScale_MatchesTheReferenceFormula(uint format)
    {
        // 3×2 magnified 2×: each source pixel becomes a 2×2 block.
        AssertMatchesReference(MixedAlphaColours(6), 3, 2, format, 6, 4, 6, 4, 0xFF203040);
    }

    [Fact]
    public void Blit_IdentityHonoursAPaddedSourceStride()
    {
        // A sub-rectangle of a wider layer: rows are further apart than
        // width × 4, which used to fall off the 1:1 copy path.
        const int sourceWidth = 3;
        const int sourceHeight = 2;
        const int sourceStride = 8 * 4;
        var colours = MixedAlphaColours(16);
        var source = new byte[sourceStride * sourceHeight];
        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan((y * sourceStride) + (x * 4)), colours[(y * sourceWidth) + x]);
            }
        }

        var target = new byte[3 * 2 * 4];
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target.AsSpan());
        pixels.Fill(0xFF203040);

        Blitter.Blit(target, 3, 2, 3 * 4, source, sourceWidth, sourceHeight, sourceStride, ShmFormats.Argb8888, 0, 0, 3, 2);

        for (var y = 0; y < sourceHeight; y++)
        {
            for (var x = 0; x < sourceWidth; x++)
            {
                var expected = Reference(colours, sourceWidth, sourceHeight, ShmFormats.Argb8888, x, y, 3, 2, 0xFF203040);
                Assert.Equal(expected, pixels[(y * 3) + x]);
            }
        }
    }

    [Theory]
    [InlineData(4, 3)] // identity
    [InlineData(6, 4)] // integer 2×
    [InlineData(7, 5)] // arbitrary sampling
    public void Blit_ClipKeepsExactlyTheUnclippedPixels(int destinationWidth, int destinationHeight)
    {
        const int sourceWidth = 3;
        const int sourceHeight = 2;
        var colours = MixedAlphaColours(sourceWidth * sourceHeight);
        var source = new byte[sourceWidth * sourceHeight * 4];
        colours.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.AsSpan()));

        var unclipped = new byte[8 * 6 * 4];
        var clipped = new byte[8 * 6 * 4];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(unclipped.AsSpan()).Fill(0xFF203040);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(clipped.AsSpan()).Fill(0xFF203040);

        var clip = new DamageRect(2, 1, 4, 3);
        Blitter.Blit(unclipped, 8, 6, 8 * 4, source, sourceWidth, sourceHeight, sourceWidth * 4, ShmFormats.Argb8888, 0, 0, destinationWidth, destinationHeight);
        Blitter.Blit(clipped, 8, 6, 8 * 4, source, sourceWidth, sourceHeight, sourceWidth * 4, ShmFormats.Argb8888, 0, 0, destinationWidth, destinationHeight, clip);

        for (var y = 0; y < 6; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var offset = ((y * 8) + x) * 4;
                if (clip.Intersects(new DamageRect(x, y, 1, 1)) && y < destinationHeight && x < destinationWidth)
                {
                    Assert.Equal(BinaryPrimitives.ReadUInt32LittleEndian(unclipped.AsSpan(offset)), BinaryPrimitives.ReadUInt32LittleEndian(clipped.AsSpan(offset)));
                }
                else
                {
                    Assert.Equal(0xFF203040u, BinaryPrimitives.ReadUInt32LittleEndian(clipped.AsSpan(offset)));
                }
            }
        }
    }

    // ---------------------------------------------------------------- scene

    [Fact]
    public void Scene_CursorMoveDamagesTwoSmallRects()
    {
        var scene = new Scene { OutputSize = (400, 300), Scale = 1 };
        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];

        // From the default hotspot: the old pointer box and the new one,
        // which merge when they touch — one small region either way.
        scene.Cursor = (100, 100);
        var count = scene.TakeDamage(400, 300, damage);
        Assert.InRange(count, 1, 2);
        Assert.Equal(0, scene.TakeDamage(400, 300, damage)); // taken once

        scene.Cursor = (120, 110);
        count = scene.TakeDamage(400, 300, damage);
        Assert.InRange(count, 1, 2);

        // Small: pointer-sized boxes, not the desktop.
        long painted = 0;
        for (var index = 0; index < count; index++)
        {
            Assert.True(damage[index].W <= 60 && damage[index].H <= 60, $"rect {damage[index]} is not a pointer-sized box");
            painted += damage[index].Area;
        }

        Assert.True(painted < 4000, $"pointer damage covered {painted} px of {400 * 300}");

        // A repeat of the same position is not a change and must not paint.
        scene.Cursor = (120, 110);
        Assert.Equal(0, scene.TakeDamage(400, 300, damage));
    }

    [Fact]
    public void Scene_UnspecifiedChangeIsAFullRepaint()
    {
        var scene = new Scene { OutputSize = (400, 300), Scale = 1 };
        scene.MarkDirty();

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        Assert.Equal(1, scene.TakeDamage(400, 300, damage));
        Assert.Equal(new DamageRect(0, 0, 400, 300), damage[0]);
    }

    [Fact]
    public void Scene_DamageRenderMatchesFullRender()
    {
        using var harness = new Harness(200, 150, scale: 1);
        var window = harness.MapWindow(120, 100, ShmFormats.Argb8888, alphaPattern: true);
        window.Place(10, 10, 120, 100);
        var scene = harness.Scene;
        scene.Cursor = (50, 40);
        harness.Settle();

        var previous = Frame(scene, 200, 150);
        scene.Cursor = (90, 70);

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = scene.TakeDamage(200, 150, damage);
        Assert.InRange(count, 1, 2);
        long painted = 0;
        for (var index = 0; index < count; index++)
        {
            painted += damage[index].Area;
        }

        Assert.True(painted < 2000, $"damage covered {painted} px");

        // Seed from the frame on screen, then repaint only the damage: the
        // result must be pixel-identical to a full repaint of the new state.
        var damaged = (byte[])previous.Clone();
        for (var index = 0; index < count; index++)
        {
            scene.RenderRegion(damaged, 200, 150, 200 * 4, frame: 1, damage[index]);
        }

        var full = Frame(scene, 200, 150);
        Assert.Equal(full, damaged);
    }

    [Fact]
    public void Scene_DamagedOpaqueWindowMatchesFullRender()
    {
        using var harness = new Harness(200, 150, scale: 2);
        var window = harness.MapWindow(100, 80, ShmFormats.Xrgb8888, alphaPattern: false);
        window.Place(0, 0, 100, 70);
        var scene = harness.Scene;
        scene.Cursor = (20, 20);
        harness.Settle();

        var previous = Frame(scene, 200, 150);
        scene.Cursor = (150, 120);

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = scene.TakeDamage(200, 150, damage);
        Assert.InRange(count, 1, 2);

        var damaged = (byte[])previous.Clone();
        for (var index = 0; index < count; index++)
        {
            scene.RenderRegion(damaged, 200, 150, 200 * 4, frame: 1, damage[index]);
        }

        Assert.Equal(Frame(scene, 200, 150), damaged);
    }

    [Fact]
    public void Scene_FullFrameSkipsTheBackgroundUnderOpaqueWindows()
    {
        using var harness = new Harness(400, 300, scale: 1);
        var window = harness.MapWindow(200, 150, ShmFormats.Xrgb8888, alphaPattern: false);
        window.Place(0, 0, 200, 150);
        harness.Scene.Cursor = (10, 10);

        var pixels = new byte[400 * 300 * 4];
        var cost = harness.Scene.RenderRegion(pixels, 400, 300, 400 * 4, frame: 0, DamageRect.For(400, 300));

        // A 200×150 opaque window hides that much desktop: the background is
        // painted everywhere else only.
        Assert.Equal((400 * 300) - (200 * 150), cost.BackgroundPixels);
        Assert.Equal(200 * 150, cost.SurfacePixels);
    }

    [Fact]
    public void Scene_ChromeIsSkippedWhenTheRegionCannotShowIt()
    {
        using var harness = new Harness(200, 150, scale: 1);
        harness.Scene.Shell = new Shell();
        harness.Scene.Cursor = (50, 20);

        var pixels = new byte[200 * 150 * 4];
        var cost = harness.Scene.RenderRegion(pixels, 200, 150, 200 * 4, frame: 0, new DamageRect(50, 18, 24, 26));

        // The region is far above the taskbar band: no chrome was drawn, so
        // the whole pointer box cost the background and nothing else.
        Assert.Equal(24 * 26, cost.BackgroundPixels);
        Assert.Equal(0, cost.SurfacePixels);

        // ...and a region that reaches the band does draw it (the bar is
        // opaque, so the seeded pixels outside the region stay correct).
        var bar = harness.Scene.RenderRegion(pixels, 200, 150, 200 * 4, frame: 0, new DamageRect(0, 120, 200, 30));
        Assert.True(bar.BackgroundPixels > 0);
    }

    [Fact]
    public void Scene_RemoveDamagesTheWholeOutput()
    {
        // A window can leave the scene without a commit (client teardown,
        // broken socket). The scene cannot say where it was, so the frame must
        // be repainted whole — a wake with no damage paints nothing, and the
        // departed window's pixels would stay up.
        var scene = new Scene { OutputSize = (400, 300), Scale = 1 };
        var surface = new SurfaceHandler(scene);
        scene.Register(surface);

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        Assert.Equal(1, scene.TakeDamage(400, 300, damage));
        Assert.Equal(new DamageRect(0, 0, 400, 300), damage[0]);

        scene.Register(new SurfaceHandler(scene));
        Assert.Equal(1, scene.TakeDamage(400, 300, damage));

        scene.Remove(surface);
        Assert.Equal(1, scene.TakeDamage(400, 300, damage));
        Assert.Equal(new DamageRect(0, 0, 400, 300), damage[0]);
    }

    [Fact]
    public void Scene_DestroyedWindowLeavesNoPixelsBehind()
    {
        using var harness = new Harness(200, 150, scale: 1);
        var window = harness.MapWindow(60, 50, ShmFormats.Xrgb8888, alphaPattern: false);
        window.Place(20, 20, 60, 50);
        var scene = harness.Scene;
        scene.Cursor = (5, 5);
        harness.Settle();

        var before = Frame(scene, 200, 150);
        var inside = ((30 * 200) + 30) * 4;
        Assert.NotEqual(
            BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(inside)),
            Background.ColourAt(30, 150));

        // The client goes away: teardown reaches Scene.Remove, which must
        // damage the whole output so the background comes back.
        harness.Client.SendRequest(8, "wl_surface", "destroy");
        CompositorSessionTests.WaitFor(() => scene.Surfaces.Count, count => count == 0);

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = scene.TakeDamage(200, 150, damage);
        Assert.Equal(1, count);
        Assert.Equal(new DamageRect(0, 0, 200, 150), damage[0]);

        var after = (byte[])before.Clone();
        scene.RenderRegion(after, 200, 150, 200 * 4, frame: 1, damage[0]);
        Assert.Equal(Background.ColourAt(30, 150), BinaryPrimitives.ReadUInt32LittleEndian(after.AsSpan(inside)));
    }

    // --------------------------------------------------------------- scanout

    [Fact]
    public void CopyRegion_SeedsExactlyTheRegion()
    {
        const int width = 16;
        const int height = 8;
        var stride = width * 4;
        var front = new byte[stride * height];
        for (var index = 0; index < front.Length; index++)
        {
            front[index] = (byte)(index % 251);
        }

        var back = new byte[stride * height];
        Array.Fill(back, (byte)0x5A);

        var copied = Scanout.CopyRegion(front, back, stride, new DamageRect(3, 2, 5, 4));

        Assert.Equal(5 * 4 * 4, copied);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * stride) + (x * 4);
                var inside = x >= 3 && x < 8 && y >= 2 && y < 6;
                for (var byteIndex = 0; byteIndex < 4; byteIndex++)
                {
                    Assert.Equal(inside ? front[offset + byteIndex] : (byte)0x5A, back[offset + byteIndex]);
                }
            }
        }
    }

    [Fact]
    public void NextClockWake_LandsOnTheMinuteBoundary()
    {
        var now = new DateTime(2026, 11, 1, 12, 34, 30, DateTimeKind.Local);
        var wait = Scanout.NextClockWake(now);
        Assert.InRange(wait.TotalSeconds, 29.9, 30.1);

        // Just past the boundary: nearly a whole minute, clamped so a stepped
        // clock can never park the loop for longer.
        var justPast = new DateTime(2026, 11, 1, 12, 34, 0, DateTimeKind.Local);
        Assert.Equal(TimeSpan.TicksPerMinute, Scanout.NextClockWake(justPast).Ticks);

        // Just before it: a tiny wait, but never so tiny it spins.
        var justBefore = new DateTime(2026, 11, 1, 12, 34, 59, 999, DateTimeKind.Local);
        Assert.InRange(Scanout.NextClockWake(justBefore).TotalMilliseconds, 50, 60);
    }

    [Fact]
    public void ScreenPower_UntilIdleCountsDownToTheBlank()
    {
        // The render loop sleeps no longer than this, so a 5-minute blank is
        // not delayed by a minute-long idle wait.
        long now = 0;
        var power = new ScreenPower(() => now);
        Assert.Equal(ScreenPower.IdleBeforeBlank, power.UntilIdle);

        now = (long)TimeSpan.FromMinutes(4).TotalMilliseconds;
        Assert.Equal(TimeSpan.FromMinutes(1), power.UntilIdle);

        now = (long)TimeSpan.FromMinutes(6).TotalMilliseconds;
        Assert.Equal(TimeSpan.Zero, power.UntilIdle);
    }

    [Fact]
    public void BufferAge_CarriesTheSkippedFramesDamage()
    {
        var age = new BufferAge();
        Span<DamageRect> repaint = stackalloc DamageRect[DamageTracker.Capacity];
        var first = new DamageRect(10, 10, 20, 20);
        var second = new DamageRect(300, 400, 20, 20);

        // Settle both buffers: each starts owing a whole repaint.
        age.MarkFull();
        Assert.Equal(1, age.Take(0, [first], 800, 600, repaint));
        Assert.Equal(new DamageRect(0, 0, 800, 600), repaint[0]);
        Assert.Equal(1, age.Take(1, [first], 800, 600, repaint));

        // Now a small change: the buffer being written last showed the frame
        // from two flips ago, so it repaints this damage and what it skipped.
        Assert.Equal(1, age.Take(0, [first], 800, 600, repaint));
        Assert.Equal(first, repaint[0]);

        var count = age.Take(1, [second], 800, 600, repaint);
        Assert.Equal(2, count);
        Assert.Contains(first, repaint[..count].ToArray());
        Assert.Contains(second, repaint[..count].ToArray());
    }

    [Fact]
    public void BufferAge_AFullFrameMakesTheNextOneFull()
    {
        var age = new BufferAge();
        Span<DamageRect> repaint = stackalloc DamageRect[DamageTracker.Capacity];
        var small = new DamageRect(10, 10, 20, 20);

        age.MarkFull();
        Assert.Equal(1, age.Take(0, [small], 800, 600, repaint));
        Assert.Equal(1, age.Take(1, [small], 800, 600, repaint));

        // A whole-output repaint into buffer 0 leaves buffer 1 (which showed
        // two frames ago) entirely stale, so the next frame is full as well.
        Assert.Equal(1, age.Take(0, [DamageRect.For(800, 600)], 800, 600, repaint));
        Assert.Equal(1, age.Take(1, [small], 800, 600, repaint));
        Assert.Equal(new DamageRect(0, 0, 800, 600), repaint[0]);

        // ...and after that the damage is small again.
        var count = age.Take(0, [small], 800, 600, repaint);
        Assert.Equal(1, count);
        Assert.Equal(small, repaint[0]);
    }

    // ------------------------------------------------------------- the win

    [Fact]
    public void DamageFrameWritesFarLessThanAFullFrame()
    {
        // The measured problem: 2880×1800 output, scale 2, a maximised
        // opaque window over the work area, and a pointer that moves.
        using var harness = new Harness(2880, 1800, scale: 2);
        var window = harness.MapWindow(1440, 852, ShmFormats.Xrgb8888, alphaPattern: false);
        window.Place(0, 0, 1440, 852);
        var scene = harness.Scene;
        scene.Cursor = (700, 400);
        harness.Settle();

        var pixels = new byte[2880 * 1800 * 4];
        var full = scene.RenderRegion(pixels, 2880, 1800, 2880 * 4, frame: 0, DamageRect.For(2880, 1800));

        scene.Cursor = (720, 410);
        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = scene.TakeDamage(2880, 1800, damage);
        Assert.True(count > 0);

        var damaged = (byte[])pixels.Clone();
        long seeded = 0;
        for (var index = 0; index < count; index++)
        {
            seeded += Scanout.CopyRegion(pixels, damaged, 2880 * 4, damage[index]);
            Assert.True(damage[index].Area < 20_000, $"pointer damage {damage[index]} is not small");
        }

        long damagePixels = 0;
        foreach (var rect in damage[..count])
        {
            damagePixels += scene.RenderRegion(damaged, 2880, 1800, 2880 * 4, frame: 1, rect).Pixels;
        }

        // Full frame: 4.9 Mpx of window over a background it fully covers,
        // plus the strip beside the taskbar band. Damaged frame: two
        // pointer-sized boxes, seeded and composited.
        Assert.True(full.Pixels > 4_000_000, $"full frame painted {full.Pixels} px");
        Assert.True(damagePixels < 5_000, $"damage frame painted {damagePixels} px");
        Assert.True(seeded < 20_000, $"damage frame seeded {seeded} bytes");
        Assert.True(full.Pixels / Math.Max(1, damagePixels) > 1000, $"ratio {full.Pixels / Math.Max(1, damagePixels)}× is not the win we expect");

        // The frame on screen, pixel for pixel, is the same either way.
        Assert.Equal(Frame(scene, 2880, 1800), damaged);
    }

    private static byte[] Frame(Scene scene, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        scene.Render(pixels, width, height, width * 4, frame: 0);
        return pixels;
    }

    /// <summary>A scene with a real client on a real socket: windows only exist through the protocol.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly List<string> _files = [];
        private readonly TestClient _client;

        internal Harness(int width, int height, int scale)
        {
            Scene = new Scene { OutputSize = (width, height), Scale = scale };
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-damage-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene));
            _server.Start();
            _client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(_client);
        }

        internal Scene Scene { get; }

        internal TestClient Client => _client;

        /// <summary>Discards whatever is pending, so a test can measure one change on its own.</summary>
        internal int Settle()
        {
            Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
            return Scene.TakeDamage(Scene.OutputSize.Width, Scene.OutputSize.Height, damage);
        }

        /// <summary>Maps a window whose content is a colour wash (optionally with per-pixel alpha).</summary>
        internal SurfaceHandler MapWindow(int width, int height, uint format, bool alphaPattern)
        {
            var content = new byte[width * height * 4];
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(content.AsSpan());
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var alpha = alphaPattern ? (uint)((x * 7) + (y * 13)) & 0xFF : 0xFF;
                    pixels[(y * width) + x] = (alpha << 24) | 0x00403020u | ((uint)x << 16) | ((uint)y << 8);
                }
            }

            var path = Path.GetTempFileName();
            File.WriteAllBytes(path, content);
            _files.Add(path);
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            _client.SendRequest(5, "wl_shm", "create_pool", WireArgument.NewId(6), WireArgument.FileDescriptor(unchecked((int)handle.DangerousGetHandle())), WireArgument.Int32(content.Length));
            _client.RegisterObject(6, "wl_shm_pool");
            _client.SendRequest(6, "wl_shm_pool", "create_buffer", WireArgument.NewId(7), WireArgument.Int32(0), WireArgument.Int32(width), WireArgument.Int32(height), WireArgument.Int32(width * 4), WireArgument.UInt32(format));
            _client.RegisterObject(7, "wl_buffer");

            _client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
            _client.RegisterObject(8, "wl_surface");
            _client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            _client.SendRequest(8, "wl_surface", "damage", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(width), WireArgument.Int32(height));
            _client.SendRequest(8, "wl_surface", "commit");

            var surface = CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(value => value.Id == 8),
                value => value is { IsMapped: true });
            return surface!;
        }

        public void Dispose()
        {
            _client.Dispose();
            _server.Dispose();
            foreach (var file in _files)
            {
                File.Delete(file);
            }
        }
    }
}