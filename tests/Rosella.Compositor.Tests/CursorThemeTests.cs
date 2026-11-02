using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The pointer theme Rosella draws itself: Xcursor files parse, libXcursor's
/// nearest-size and inheritance rules are followed (native pixels, never
/// rescaled), and the settings chain matches what GTK clients read — so the
/// desktop pointer and Chrome's set_cursor bitmap come from the same art.
/// </summary>
public sealed class CursorThemeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rosella-cursor-{Guid.NewGuid():N}");

    public CursorThemeTests()
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
            // A test left a file open; the temp directory is disposable anyway.
        }
    }

    [Fact]
    public void XCursorFile_ParsesEveryImageWithItsHotspot()
    {
        var bytes = Encode(
            Image(24, 1, 0, [0xFF0000FFu, 0xFF00FF00u, 0xFFFF0000u, 0xFFFFFFFFu]),
            Image(48, 2, 2, new uint[4]));

        Assert.True(XCursorFile.TryParse(bytes, out var images));
        Assert.Equal(2, images.Count);
        Assert.Equal(24, images[0].Size);
        Assert.Equal((2, 2), (images[0].Width, images[0].Height));
        Assert.Equal((1, 0), (images[0].HotspotX, images[0].HotspotY));
        Assert.Equal(0xFF00FF00u, images[0].Pixels[1]);
        Assert.Equal(48, images[1].Size);
        Assert.Equal((2, 2), (images[1].HotspotX, images[1].HotspotY));
    }

    [Fact]
    public void XCursorFile_RejectsAnotherFormat()
    {
        Assert.False(XCursorFile.TryParse("not an xcursor file"u8, out var images));
        Assert.Empty(images);
    }

    [Fact]
    public void XCursorFile_RejectsTruncatedPixels()
    {
        var bytes = Encode(Image(24, 0, 0, new uint[4]));
        // Claim 8x8 pixels where the file carries 2x2.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + 12 + 16), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + 12 + 20), 8);

        Assert.False(XCursorFile.TryParse(bytes, out var images));
        Assert.Empty(images);
    }

    [Fact]
    public void XCursorFile_RejectsAnImpossibleTableOfContents()
    {
        var bytes = Encode(Image(24, 1, 1, new uint[4]));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 1_000_000);

        Assert.False(XCursorFile.TryParse(bytes, out _));
    }

    [Fact]
    public void XCursorFile_SkipsAChunkWhoseTypeDisagreesWithTheTable()
    {
        var bytes = Encode(Image(24, 1, 1, new uint[4]));
        // The TOC says image; the chunk says comment.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16 + 12 + 4), 0xFFFE0001);

        Assert.False(XCursorFile.TryParse(bytes, out _));
    }

    [Fact]
    public void XCursorFile_SkipsAHotspotOutsideTheImage()
    {
        var onEdge = Encode((24, 2, 2, 2, 1, new uint[4]));
        Assert.True(XCursorFile.TryParse(onEdge, out _), "a hotspot on the right edge is legal");

        var outside = Encode((24, 2, 2, 3, 1, new uint[4]));
        Assert.False(XCursorFile.TryParse(outside, out _));
    }

    [Fact]
    public void CursorTheme_UsesTheNearestImageAndItsNativeSize()
    {
        var theme = Build("Nearest", ("left_ptr", [Small(0xFF112233u), Large(0xFF445566u)]));

        var at24 = theme!.Pointer(scale: 1);
        var at48 = theme.Pointer(scale: 2);

        Assert.NotNull(at24);
        Assert.NotNull(at48);
        Assert.Equal(24, at24!.Width);
        Assert.Equal(0xFF112233u, at24.Pixels[0]);
        Assert.Equal(48, at48!.Width);
        Assert.Equal(0xFF445566u, at48.Pixels[0]);
    }

    [Fact]
    public void CursorTheme_PicksTheClosestSizeAboveOrBelow()
    {
        // 40 is 8 from 48 and 16 from 24: libXcursor chooses 48 and does not
        // rescale, so the pointer is a 48-pixel image.
        var theme = Build("Closest", ("left_ptr", [Small(0xFF112233u), Large(0xFF445566u)]));

        var bitmap = theme!.Image("left_ptr", 40);

        Assert.NotNull(bitmap);
        Assert.Equal(48, bitmap!.Width);
        Assert.Equal(0xFF445566u, bitmap.Pixels[0]);
    }

    [Fact]
    public void CursorTheme_FirstImageWinsATie()
    {
        var first24 = Build("Tie24", ("left_ptr", [Small(0xFF112233u), Large(0xFF445566u)]));
        var first48 = Build("Tie48", ("left_ptr", [Large(0xFF445566u), Small(0xFF112233u)]));

        // 36 is 12 from each: file order decides, as libXcursor does.
        Assert.Equal(24, first24!.Image("left_ptr", 36)!.Width);
        Assert.Equal(48, first48!.Image("left_ptr", 36)!.Width);
    }

    [Fact]
    public void CursorTheme_KeepsTheNativeSizeWhenNothingIsClose()
    {
        var theme = Build("OnlyLarge", ("left_ptr", [Large(0xFF778899u)]));

        var bitmap = theme!.Pointer(scale: 1);

        Assert.NotNull(bitmap);
        Assert.Equal(48, bitmap!.Width);
        Assert.Equal(48, bitmap.Height);
    }

    [Fact]
    public void CursorTheme_FollowsInheritsForAMissingShape()
    {
        var inherited = new ThemeDirectory(_root, "Base");
        inherited.Shape("left_ptr", [Small(0xFFFFFFFFu)]);

        var themeDirectory = new ThemeDirectory(_root, "Child");
        themeDirectory.Inherits("Base");

        var theme = CursorTheme.Open("Child", 24, [_root]);

        Assert.NotNull(theme);
        Assert.Equal(24, theme!.Pointer(scale: 1)!.Width);
    }

    [Fact]
    public void CursorTheme_TriesDefaultWhenLeftPtrIsMissing()
    {
        var theme = Build("DefaultOnly", ("default", [Small(0xFFFFFFFFu)]));

        Assert.NotNull(theme!.Pointer(scale: 1));
    }

    [Fact]
    public void CursorTheme_ReturnsNullForAMissingTheme()
    {
        Assert.Null(CursorTheme.Open("NoSuchTheme", 24, [_root]));
    }

    [Fact]
    public void CursorTheme_RejectsATraversingName()
    {
        Assert.Null(CursorTheme.Open("../Rosella-Cursor", 24, [_root]));
    }

    [Fact]
    public void CursorSettings_PrefersGSettings()
    {
        var choice = CursorSettings.Resolve(
            key => key == "cursor-theme" ? "'Rosella-Ring'" : "32",
            home: _root,
            configHome: _root,
            systemSettings: []);

        Assert.Equal(new CursorSettings.Choice("Rosella-Ring", 32, null), choice);
    }

    [Fact]
    public void CursorSettings_FallsBackToGtkSettingsIni()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "gtk-3.0"));
        File.WriteAllText(
            Path.Combine(config, "gtk-3.0", "settings.ini"),
            "[Settings]\ngtk-cursor-theme-name=Rosella-Ring\ngtk-cursor-theme-size=48\n");

        var choice = CursorSettings.Resolve(_ => null, home: _root, configHome: config, systemSettings: []);

        Assert.Equal(new CursorSettings.Choice("Rosella-Ring", 48, null), choice);
    }

    [Fact]
    public void CursorSettings_KeepsTheIniThemeAsAFallback()
    {
        var config = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(config, "gtk-3.0"));
        File.WriteAllText(
            Path.Combine(config, "gtk-3.0", "settings.ini"),
            "[Settings]\ngtk-cursor-theme-name=Breeze\n");

        // A GNOME schema default can name a theme this machine lacks; the
        // ini name must survive as the next candidate.
        var choice = CursorSettings.Resolve(
            key => key == "cursor-theme" ? "'Adwaita'" : null,
            home: _root,
            configHome: config,
            systemSettings: []);

        Assert.Equal("Adwaita", choice.Theme);
        Assert.Equal("Breeze", choice.Fallback);
    }

    [Fact]
    public void CursorSettings_DefaultsToTheDefaultThemeAt24()
    {
        var choice = CursorSettings.Resolve(_ => null, home: _root, configHome: _root, systemSettings: []);

        Assert.Equal(new CursorSettings.Choice(CursorSettings.DefaultTheme, 24, null), choice);
    }

    [Fact]
    public void CursorSettings_DoesNotQueryTheSizeWithoutABus()
    {
        var keys = new List<string>();
        CursorSettings.Resolve(
            key =>
            {
                keys.Add(key);
                return null;
            },
            home: _root,
            configHome: _root,
            systemSettings: []);

        Assert.Equal(["cursor-theme"], keys);
    }

    [Fact]
    public void Scene_DrawsTheThemedPointerAtItsHotspot()
    {
        var theme = Build("Scene", ("left_ptr", [(24, 2, 2, 1, 1, [0xFFFF00FFu, 0xFF00FF00u, 0xFF0000FFu, 0xFFFFFFFFu])]));
        var scene = new Scene
        {
            OutputSize = (64, 48),
            Scale = 1,
            Cursors = theme,
            Cursor = (10, 10),
        };

        var pixels = Render(scene, 64, 48);

        // Top-left of the 2x2 image lands one pixel above and left of the
        // hotspot, so the pointer pixel itself is the white corner.
        Assert.Equal(0xFFFF00FFu, Pixel(pixels, 64, 9, 9));
        Assert.Equal(0xFFFFFFFFu, Pixel(pixels, 64, 10, 10));
        Assert.NotEqual(0xFFFFFFFFu, Pixel(pixels, 64, 30, 20));
    }

    [Fact]
    public void Scene_DrawsTheThemedPointerOnAScaledOutput()
    {
        // The theme ships no 48: the nearest image (24) is used at its
        // native 2x2, as wl_cursor_theme_load would, so the hotspot stays
        // (1, 1) on a scale-2 output.
        var theme = Build("ScaledScene", ("left_ptr", [(24, 2, 2, 1, 1, [0xFFFF00FFu, 0xFF00FF00u, 0xFF0000FFu, 0xFFFFFFFFu])]));
        var scene = new Scene
        {
            OutputSize = (64, 48),
            Scale = 2,
            Cursors = theme,
            Cursor = (10, 10),
        };

        var pixels = Render(scene, 128, 96);

        Assert.Equal(0xFFFF00FFu, Pixel(pixels, 128, 19, 19));
        Assert.Equal(0xFFFFFFFFu, Pixel(pixels, 128, 20, 20));
    }

    [Fact]
    public void Scene_DamagesTheWholeThemedPointerWithAnOddHotspot()
    {
        // Hotspot (3, 1) at scale 2 is odd in physical pixels: dividing it
        // down and back would miss the left column of the image.
        var theme = Build("Odd", ("left_ptr", [(24, 24, 24, 3, 1, Fill(24, 24, 0xFFFFFFFFu))]));
        var scene = new Scene
        {
            OutputSize = (64, 48),
            Scale = 2,
            Cursors = theme,
            Cursor = (10, 10),
        };

        Span<DamageRect> damage = stackalloc DamageRect[DamageTracker.Capacity];
        var count = scene.TakeDamage(128, 96, damage);

        var painted = false;
        for (var index = 0; index < count; index++)
        {
            var rect = damage[index];
            if (Contains(rect, 17, 19) && Contains(rect, 40, 42))
            {
                painted = true;
            }
        }

        Assert.True(painted, "damage does not cover the pointer's physical box");
    }

    [Fact]
    public void Scene_PartialRegionDrawsThePointerLikeAFullFrame()
    {
        var theme = Build("Region", ("left_ptr", [(24, 2, 2, 1, 1, [0xFFFF00FFu, 0xFF00FF00u, 0xFF0000FFu, 0xFFFFFFFFu])]));
        var scene = new Scene
        {
            OutputSize = (64, 48),
            Scale = 1,
            Cursors = theme,
            Cursor = (10, 10),
        };

        var full = Render(scene, 64, 48);
        var region = new DamageRect(8, 8, 4, 4);
        var seeded = new byte[64 * 48 * 4];
        Array.Fill(seeded, (byte)0x11);
        scene.RenderRegion(seeded, 64, 48, 64 * 4, frame: 1, region);

        for (var y = region.Y; y < region.Y + region.H; y++)
        {
            for (var x = region.X; x < region.X + region.W; x++)
            {
                Assert.Equal(Pixel(full, 64, x, y), Pixel(seeded, 64, x, y));
            }
        }

        // Outside the damage the seeded buffer is untouched.
        Assert.Equal(0x11111111u, Pixel(seeded, 64, 30, 20));
    }

    [Fact]
    public void Scene_FallsBackToTheArrowWhenTheThemeHasNoPointer()
    {
        var theme = Build("NoPointer", ("hand2", [Small(0xFFFFFFFFu)]));
        var scene = new Scene { OutputSize = (64, 48), Scale = 1, Cursors = theme, Cursor = (10, 10) };

        var pixels = Render(scene, 64, 48);

        // The sprite's first pixel is the black outline at the pointer.
        Assert.Equal(0xFF000000u, Pixel(pixels, 64, 10, 10));
    }

    [Fact]
    public void Scene_FallsBackToTheArrowWithoutATheme()
    {
        var scene = new Scene { OutputSize = (64, 48), Scale = 1, Cursor = (10, 10) };
        var pixels = Render(scene, 64, 48);

        Assert.Equal(0xFF000000u, Pixel(pixels, 64, 10, 10));
    }

    private static byte[] Render(Scene scene, int width, int height)
    {
        var pixels = new byte[width * height * 4];
        scene.Render(pixels, width, height, width * 4, frame: 0);
        return pixels;
    }

    private static uint Pixel(byte[] pixels, int width, int x, int y) =>
        MemoryMarshal.Read<uint>(pixels.AsSpan((y * width * 4) + (x * 4)));

    private static bool Contains(in DamageRect rect, int x, int y) =>
        x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;

    private CursorTheme? Build(string name, params (string Shape, (int Size, int Width, int Height, int XHot, int YHot, uint[] Pixels)[] Images)[] shapes)
    {
        var directory = new ThemeDirectory(_root, name);
        foreach (var (shape, images) in shapes)
        {
            directory.Shape(shape, images);
        }

        return CursorTheme.Open(name, 24, [_root]);
    }

    private static (int, int, int, int, int, uint[]) Small(uint colour) =>
        (24, 24, 24, 1, 1, Fill(24, 24, colour));

    private static (int, int, int, int, int, uint[]) Large(uint colour) =>
        (48, 48, 48, 2, 2, Fill(48, 48, colour));

    private static uint[] Fill(int width, int height, uint colour)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, colour);
        return pixels;
    }

    private static (int, int, int, int, int, uint[]) Image(int size, int xhot, int yhot, uint[] pixels) =>
        (size, 2, 2, xhot, yhot, pixels);

    /// <summary>A theme directory on disk, built the way a cursor package is: cursors/ plus index.theme.</summary>
    private sealed class ThemeDirectory
    {
        internal ThemeDirectory(string root, string name)
        {
            FullPath = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(FullPath, "cursors"));
        }

        internal string FullPath { get; }

        internal void Shape(string shape, params (int Size, int Width, int Height, int XHot, int YHot, uint[] Pixels)[] images) =>
            File.WriteAllBytes(Path.Combine(FullPath, "cursors", shape), Encode(images));

        internal void Inherits(string names) =>
            File.WriteAllText(Path.Combine(FullPath, "index.theme"), $"[Icon Theme]\nName={Path.GetFileName(FullPath)}\nInherits={names}\n");
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
}