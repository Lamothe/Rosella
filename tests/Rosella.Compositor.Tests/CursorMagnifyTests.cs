using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Shake to find the pointer: fast strokes grow it for a second, a drag
/// never triggers it, and the enlargement uses the theme's own larger art
/// so it stays crisp — then the desktop shrinks back on its own, even with
/// no other input to wake the render loop.
/// </summary>
public sealed class CursorMagnifyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rosella-magnify-{Guid.NewGuid():N}");

    public CursorMagnifyTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the system cleans its temp directory.
        }
    }

    // --- The gesture ------------------------------------------------------

    [Fact]
    public void Shake_ThreeStrokesInsideTheWindowFire()
    {
        var shake = new CursorShake();

        // A 100 px bolt right, left, right, left: each turn is a stroke.
        Assert.False(shake.Add(100, 0, 1000));
        Assert.False(shake.Add(-100, 0, 1120));
        Assert.False(shake.Add(100, 0, 1240));
        Assert.True(shake.Add(-100, 0, 1360));

        // One more stroke must not fire again: a fired shake starts over,
        // otherwise holding the gesture would re-extend the hold forever.
        Assert.False(shake.Add(100, 0, 1480));
    }

    [Fact]
    public void Shake_TwoStrokesAreJustAPointingWobble()
    {
        var shake = new CursorShake();

        shake.Add(100, 0, 1000);
        shake.Add(-100, 0, 1100);
        Assert.False(shake.Add(100, 0, 1200));
    }

    [Fact]
    public void Shake_SmallWobblesDoNotCount()
    {
        var shake = new CursorShake();

        // Aiming jitter: each leg is far below the stroke threshold.
        for (var index = 0; index < 20; index++)
        {
            Assert.False(shake.Add(index % 2 == 0 ? 12 : -12, 0, (uint)(1000 + (index * 40))));
        }
    }

    [Fact]
    public void Shake_APauseStartsOver()
    {
        var shake = new CursorShake();

        shake.Add(100, 0, 1000);
        shake.Add(-100, 0, 1120);

        // A click or a rest between strokes: the count must not carry over.
        shake.Add(100, 0, 3000);
        shake.Add(-100, 0, 3120);
        Assert.False(shake.Add(100, 0, 3240));
        Assert.True(shake.Add(-100, 0, 3360), "three strokes after the pause still fire");
    }

    [Fact]
    public void Shake_StrokesSpreadTooWideDoNotFire()
    {
        var shake = new CursorShake();

        // 400 ms between reversals is a slow deliberate zig-zag, not a
        // "where is it?" shake: the window restarts each time.
        shake.Add(100, 0, 1000);
        Assert.False(shake.Add(-100, 0, 1400));
        Assert.False(shake.Add(100, 0, 1800));
        Assert.False(shake.Add(-100, 0, 2200));
    }

    [Fact]
    public void Shake_CarriesMotionAcrossSamples()
    {
        var shake = new CursorShake();

        // The device reports in small deltas; the stroke is their sum.
        for (var index = 0; index < 5; index++)
        {
            shake.Add(8, 0, (uint)(1000 + (index * 8)));
        }

        shake.Add(-45, 0, 1040);
        shake.Add(45, 0, 1080);
        Assert.True(shake.Add(-45, 0, 1120));
    }

    [Fact]
    public void Shake_VerticalShakeWorksToo()
    {
        var shake = new CursorShake();

        Assert.False(shake.Add(0, 100, 1000));
        Assert.False(shake.Add(0, -100, 1100));
        Assert.False(shake.Add(0, 100, 1200));
        Assert.True(shake.Add(0, -100, 1300));
    }

    // --- The gesture through the router -----------------------------------

    [Fact]
    public void Router_AShakeMagnifiesThePointer()
    {
        using var harness = new RouterHarness();
        harness.Scene.Now = () => 0;

        harness.Shake();

        Assert.Equal(CursorMagnifyTests.Hold, harness.Scene.UntilPointerShrinks);
    }

    [Fact]
    public void Router_ADragDoesNotMagnify()
    {
        using var harness = new RouterHarness();
        harness.Router.ProcessEvent(new InputEvent(1, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyPressed));

        harness.Shake();

        Assert.Null(harness.Scene.UntilPointerShrinks);
    }

    [Fact]
    public void Router_ReleasingOneButtonOfAChordLeavesTheDragSuppressed()
    {
        using var harness = new RouterHarness();
        harness.Router.ProcessEvent(new InputEvent(1, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(1, 0, InputConstants.EventKey, InputConstants.ButtonRight, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(2, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyReleased));

        harness.Shake();

        Assert.Null(harness.Scene.UntilPointerShrinks);

        harness.Router.ProcessEvent(new InputEvent(3, 0, InputConstants.EventKey, InputConstants.ButtonRight, InputConstants.KeyReleased));
        harness.Shake(4000);
        Assert.NotNull(harness.Scene.UntilPointerShrinks);
    }

    [Fact]
    public void Router_AClickBetweenStrokesStartsTheCountOver()
    {
        using var harness = new RouterHarness();

        // Right, left (one reversal), click, right, left: without the
        // click reset the last turn would be the third reversal and fire.
        harness.Router.ProcessEvent(RouterHarness.At(1000, InputConstants.RelativeX, 100));
        harness.Router.ProcessEvent(RouterHarness.At(1040, InputConstants.RelativeX, -100));
        harness.Click(1080);
        harness.Router.ProcessEvent(RouterHarness.At(1120, InputConstants.RelativeX, 100));
        harness.Router.ProcessEvent(RouterHarness.At(1160, InputConstants.RelativeX, -100));

        Assert.Null(harness.Scene.UntilPointerShrinks);
    }

    [Fact]
    public void Router_ClicksDoNotSurviveAVtSwitch()
    {
        using var harness = new RouterHarness();
        harness.Router.ProcessEvent(new InputEvent(1, 0, InputConstants.EventKey, InputConstants.ButtonRight, InputConstants.KeyPressed));

        // The release arrives on another VT and is dropped; the reset must
        // re-arm the gesture or a button held across the switch stays stuck.
        harness.Router.ResetInputState();

        harness.Shake();
        Assert.NotNull(harness.Scene.UntilPointerShrinks);
    }

    [Fact]
    public void Router_AfterTheDragReleaseAShakeWorksAgain()
    {
        using var harness = new RouterHarness();
        harness.Router.ProcessEvent(new InputEvent(1, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyPressed));
        harness.Shake();
        harness.Router.ProcessEvent(new InputEvent(2, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyReleased));

        harness.Shake(4000);

        Assert.NotNull(harness.Scene.UntilPointerShrinks);
    }

    // --- Drawing ----------------------------------------------------------

    [Fact]
    public void Scene_MagnifiedPointerUsesTheThemesLargerArt()
    {
        // The theme ships 24 and 72: the enlargement must pick 72 (native),
        // not stretch the 24 px image.
        var theme = Theme(("left_ptr", [(24, 24, 24, 12, 12, Fill(24, 24, 0xFFFF0000u)), (72, 72, 72, 36, 36, Fill(72, 72, 0xFF00FF00u))]));
        var scene = new Scene { OutputSize = (256, 256), Scale = 1, Cursors = theme, Cursor = (128, 128), Now = () => 1000 };

        scene.MagnifyCursor();
        var pixels = Render(scene, 256, 256);

        // The 72 px image covers (92, 92)..(164, 164); the small one would
        // have stopped 24 px from the hotspot.
        Assert.Equal(0xFF00FF00u, Pixel(pixels, 256, 128 + 30, 128));
        Assert.Equal(0xFF00FF00u, Pixel(pixels, 256, 128, 128 - 30));
    }

    [Fact]
    public void Scene_MagnifiedPointerShrinksBackAfterTheHold()
    {
        var theme = Theme(("left_ptr", [(24, 24, 24, 12, 12, Fill(24, 24, 0xFFFF0000u)), (72, 72, 72, 36, 36, Fill(72, 72, 0xFF00FF00u))]));
        long now = 1000;
        var scene = new Scene { OutputSize = (256, 256), Scale = 1, Cursors = theme, Cursor = (128, 128), Now = () => now };

        scene.MagnifyCursor();
        Assert.Equal(Hold, scene.UntilPointerShrinks);

        now += (long)Hold.TotalMilliseconds;

        // The deadline has passed: the loop wakes, the whole frame is
        // damaged (the large art has to be painted over), and the next
        // render shows the normal pointer again.
        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = scene.TakeDamage(256, 256, damage);
        var full = false;
        for (var index = 0; index < count; index++)
        {
            full |= damage[index] == DamageRect.For(256, 256);
        }

        Assert.True(full, "the whole frame must be repainted to erase the large pointer");

        var pixels = Render(scene, 256, 256);
        Assert.Equal(0xFFFF0000u, Pixel(pixels, 256, 128 + 10, 128));
        Assert.NotEqual(0xFF00FF00u, Pixel(pixels, 256, 128 + 30, 128));
        Assert.Null(scene.UntilPointerShrinks);
    }

    [Fact]
    public void Scene_ShakingAgainExtendsTheHold()
    {
        long now = 5000;
        var scene = new Scene { OutputSize = (64, 48), Scale = 1, Now = () => now };

        scene.MagnifyCursor();
        now += 600;
        scene.MagnifyCursor();

        Assert.Equal(Hold, scene.UntilPointerShrinks);
    }

    [Fact]
    public void Scene_MagnifiedArrowKeepsTheHotspotAtThePointer()
    {
        // No theme: the built-in arrow, drawn at three times its size. The
        // 17x16 sprite reaches 48 px down when enlarged; the same spot is
        // desktop in the normal render.
        var scene = new Scene { OutputSize = (64, 64), Scale = 1, Cursor = (30, 30), Cursors = null };
        var normal = Render(scene, 64, 64);

        scene.MagnifyCursor();
        var enlarged = Render(scene, 64, 64);

        Assert.Equal(0xFF000000u, Pixel(enlarged, 64, 30, 30));
        Assert.Equal(0xFF000000u, Pixel(enlarged, 64, 30, 30 + 18));
        Assert.NotEqual(0xFF000000u, Pixel(normal, 64, 30, 30 + 18));
    }

    [Fact]
    public void Scene_ASingleSizeThemeIsStretchedForTheShake()
    {
        // Only 24 px art: no larger image exists, so the gesture stretches
        // the one there is rather than doing nothing.
        var theme = Theme(("left_ptr", [(24, 24, 24, 12, 12, Fill(24, 24, 0xFFFF0000u))]));
        var scene = new Scene { OutputSize = (256, 256), Scale = 1, Cursors = theme, Cursor = (128, 128), Now = () => 0 };

        scene.MagnifyCursor();
        var pixels = Render(scene, 256, 256);

        Assert.Equal(0xFFFF0000u, Pixel(pixels, 256, 128 + 30, 128));
        Assert.Equal(0xFFFF0000u, Pixel(pixels, 256, 128, 128 - 30));
    }

    // --- The idle wake ----------------------------------------------------

    [Fact]
    public void IdleWait_WakesForTheShrinkingPointer()
    {
        // Just past a minute boundary the clock alone would sleep ~60 s.
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);
        var clockOnly = Scanout.IdleWait(now, untilBlank: null, untilShrink: null);
        Assert.True(clockOnly > TimeSpan.FromSeconds(30), "the clock bound is the minute");

        var withShrink = Scanout.IdleWait(now, untilBlank: null, untilShrink: TimeSpan.FromSeconds(1));

        Assert.Equal(TimeSpan.FromSeconds(1), withShrink);
    }

    [Fact]
    public void IdleWait_NeverSpinsOnAnImminentShrink()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

        Assert.Equal(TimeSpan.FromMilliseconds(50), Scanout.IdleWait(now, untilBlank: null, untilShrink: TimeSpan.Zero));
    }

    [Fact]
    public void IdleWait_TheEarlierOfBlankAndShrinkWins()
    {
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Local);

        Assert.Equal(TimeSpan.FromSeconds(2), Scanout.IdleWait(now, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(2), Scanout.IdleWait(now, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)));
    }

    private static readonly TimeSpan Hold = Scene.MagnifyHold;

    private static byte[] Render(Scene scene, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        scene.Render(pixels, width, height, width * 4, frame: 0);
        return pixels;
    }

    private static uint Pixel(byte[] pixels, int width, int x, int y) =>
        MemoryMarshal.Read<uint>(pixels.AsSpan((y * width * 4) + (x * 4)));

    private static uint[] Fill(int width, int height, uint colour)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, colour);
        return pixels;
    }

    private CursorTheme? Theme(params (string Shape, (int Size, int Width, int Height, int XHot, int YHot, uint[] Pixels)[] Images)[] shapes)
    {
        var directory = Path.Combine(_root, "Shake");
        Directory.CreateDirectory(Path.Combine(directory, "cursors"));
        foreach (var (shape, images) in shapes)
        {
            File.WriteAllBytes(Path.Combine(directory, "cursors", shape), Encode(images));
        }

        return CursorTheme.Open("Shake", 24, [_root]);
    }

    private static byte[] Encode(params (int Size, int Width, int Height, int XHot, int YHot, uint[] Pixels)[] images)
    {
        const int header = 16;
        const int tocEntry = 12;
        const int imageHeader = 36;
        const uint imageType = 0xFFFD0002;

        var offsets = new int[images.Length];
        var length = header + (images.Length * tocEntry);
        for (var index = 0; index < images.Length; index++)
        {
            offsets[index] = length;
            length += imageHeader + (images[index].Width * images[index].Height * 4);
        }

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x72756358);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], header);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)images.Length);

        for (var index = 0; index < images.Length; index++)
        {
            var entry = span[(header + (index * tocEntry))..];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, imageType);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)images[index].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)offsets[index]);
        }

        for (var index = 0; index < images.Length; index++)
        {
            var (size, width, height, xhot, yhot, pixels) = images[index];
            var chunk = span[offsets[index]..];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk, imageHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], imageType);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[8..], (uint)size);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[12..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[16..], (uint)width);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[20..], (uint)height);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[24..], (uint)xhot);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[28..], (uint)yhot);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[32..], 0);

            var data = chunk[imageHeader..];
            for (var pixel = 0; pixel < pixels.Length; pixel++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data[(pixel * 4)..], pixels[pixel]);
            }
        }

        return bytes;
    }

    /// <summary>A router with no client: just the pointer policy under test.</summary>
    private sealed class RouterHarness : IDisposable
    {
        public RouterHarness()
        {
            Scene = new Scene { OutputSize = (1000, 700), Scale = 1, Now = () => 0 };
            Router = new InputRouter(Scene, new SeatState(), () => { });
            Router.PlaceCursor(500, 350);
        }

        public Scene Scene { get; }

        public InputRouter Router { get; }

        /// <summary>Four strokes of 100 logical px, 120 ms apart.</summary>
        public void Shake(uint start = 1000)
        {
            for (var index = 0; index < 4; index++)
            {
                var delta = index % 2 == 0 ? 100 : -100;
                Router.ProcessEvent(At(start + (uint)(index * 120), InputConstants.RelativeX, delta));
            }
        }

        public void Click(uint timeMs)
        {
            Router.ProcessEvent(At(timeMs, InputConstants.ButtonLeft, InputConstants.KeyPressed, InputConstants.EventKey));
            Router.ProcessEvent(At(timeMs + 30, InputConstants.ButtonLeft, InputConstants.KeyReleased, InputConstants.EventKey));
        }

        public void Dispose()
        {
        }

        internal static InputEvent At(uint timeMs, ushort code, int value, ushort type = InputConstants.EventRelative) =>
            new(timeMs / 1000, (timeMs % 1000) * 1000, type, code, value);
    }
}