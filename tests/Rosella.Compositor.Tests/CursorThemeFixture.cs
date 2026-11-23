using System.Buffers.Binary;

namespace Rosella.Compositor.Tests;

/// <summary>One image inside a test Xcursor file.</summary>
internal readonly record struct XCursorSpec(int Size, int Width, int Height, int HotspotX, int HotspotY, uint[] Pixels);

/// <summary>
/// Writes throwaway Xcursor themes for tests, in the real container format,
/// so the parser and every caller of it are exercised against files rather
/// than a mock.
/// </summary>
internal static class CursorThemeFixture
{
    /// <summary>A solid image of one colour (premultiplied, so opaque).</summary>
    internal static uint[] Fill(int width, int height, uint colour)
    {
        var pixels = new uint[width * height];
        Array.Fill(pixels, colour);
        return pixels;
    }

    /// <summary>
    /// Writes <paramref name="shapes"/> into a theme named <paramref name="name"/>
    /// under <paramref name="root"/> and opens it at the given logical size.
    /// </summary>
    internal static CursorTheme Create(
        string root,
        string name,
        int size,
        params (string Shape, XCursorSpec[] Images)[] shapes)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(Path.Combine(directory, "cursors"));
        foreach (var (shape, images) in shapes)
        {
            File.WriteAllBytes(Path.Combine(directory, "cursors", shape), Encode(images));
        }

        return CursorTheme.Open(name, size, [root])
            ?? throw new InvalidOperationException($"the fixture theme {name} did not open");
    }

    private static byte[] Encode(XCursorSpec[] images)
    {
        const int Header = 16;
        const int TocEntry = 12;
        const int ImageHeader = 36;
        const uint ImageType = 0xFFFD0002;

        // A chunk is located by the next header's offset (or EOF), so extra
        // pixels in an image would spill into the following chunk.
        foreach (var image in images)
        {
            if (image.Pixels.Length != image.Width * image.Height)
            {
                throw new ArgumentException(
                    $"a {image.Width}x{image.Height} image needs {image.Width * image.Height} pixels, got {image.Pixels.Length}");
            }
        }

        var offsets = new int[images.Length];
        var length = Header + (images.Length * TocEntry);
        for (var index = 0; index < images.Length; index++)
        {
            offsets[index] = length;
            length += ImageHeader + (images[index].Width * images[index].Height * 4);
        }

        var bytes = new byte[length];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, 0x72756358);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Header);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], 0x00010000);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)images.Length);

        for (var index = 0; index < images.Length; index++)
        {
            var entry = span[(Header + (index * TocEntry))..];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, ImageType);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)images[index].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)offsets[index]);
        }

        for (var index = 0; index < images.Length; index++)
        {
            var image = images[index];
            var chunk = span[offsets[index]..];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk, ImageHeader);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], ImageType);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[8..], (uint)image.Size);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[12..], 1);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[16..], (uint)image.Width);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[20..], (uint)image.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[24..], (uint)image.HotspotX);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[28..], (uint)image.HotspotY);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[32..], 0);

            var data = chunk[ImageHeader..];
            for (var pixel = 0; pixel < image.Pixels.Length; pixel++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data[(pixel * 4)..], image.Pixels[pixel]);
            }
        }

        return bytes;
    }
}