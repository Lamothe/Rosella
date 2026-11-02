using System.Buffers.Binary;

namespace Rosella.Compositor;

/// <summary>
/// One image from an Xcursor file: the nominal size it was drawn for, the
/// bitmap, and the hotspot in bitmap pixels. Pixels are premultiplied ARGB,
/// exactly as the file stores them and as wl_shm's argb8888 defines them.
/// </summary>
internal readonly record struct XCursorImage(int Size, int Width, int Height, int HotspotX, int HotspotY, uint[] Pixels);

/// <summary>
/// Reads the Xcursor container — the format of every cursor theme under
/// /usr/share/icons and libXcursor's own input. A header names a table of
/// contents; each entry points at a chunk. Only image chunks are kept
/// (comments and dpi hints are skipped), which is what the loaders behind
/// wl_cursor_theme_load hand to clients, so Rosella sees the same art.
/// </summary>
internal static class XCursorFile
{
    /// <summary>"Xcur", little-endian.</summary>
    private const uint Magic = 0x72756358;

    /// <summary>The image chunk type from the Xcursor file format.</summary>
    private const uint ImageChunk = 0xFFFD0002;

    private const int HeaderSize = 16;
    private const int TocEntrySize = 12;
    private const int ImageHeaderSize = 36;

    /// <summary>
    /// Caps that keep a hostile or corrupt file from allocating wildly.
    /// Animated cursors carry one image per frame; 512 covers the richest
    /// themes while the pixel budget bounds total memory regardless of count.
    /// </summary>
    private const int MaxImages = 512;
    private const int MaxDimension = 1024;
    private const long MaxTotalPixels = 16L * 1024 * 1024;

    /// <summary>
    /// Parses the container. Returns false for a file that is not Xcursor or
    /// yields no usable image; individual malformed chunks are skipped rather
    /// than failing the whole file, the way libXcursor tolerates themes with
    /// odd extra chunks.
    /// </summary>
    internal static bool TryParse(ReadOnlySpan<byte> bytes, out List<XCursorImage> images)
    {
        images = [];
        if (bytes.Length < HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic)
        {
            return false;
        }

        var header = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (header < HeaderSize
            || header > bytes.Length
            || count > (bytes.Length - header) / TocEntrySize)
        {
            return false;
        }

        var totalPixels = 0L;
        for (var index = 0L; index < count; index++)
        {
            var entry = header + (index * TocEntrySize);
            var type = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)entry..]);
            if (type != ImageChunk)
            {
                continue;
            }

            if (images.Count >= MaxImages)
            {
                break;
            }

            var subtype = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(entry + 4)..]);
            var position = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(int)(entry + 8)..]);
            if (position > bytes.Length - ImageHeaderSize)
            {
                continue;
            }

            var chunk = bytes[(int)position..];
            var chunkHeader = BinaryPrimitives.ReadUInt32LittleEndian(chunk);

            // The chunk must restate the TOC entry, as libXcursor requires:
            // an entry that points at a comment chunk is not an image.
            if (chunkHeader < ImageHeaderSize
                || chunkHeader > chunk.Length
                || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != type
                || BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != subtype)
            {
                continue;
            }

            var width = BinaryPrimitives.ReadUInt32LittleEndian(chunk[16..]);
            var height = BinaryPrimitives.ReadUInt32LittleEndian(chunk[20..]);
            if (subtype is 0 or > MaxDimension || width is 0 or > MaxDimension || height is 0 or > MaxDimension)
            {
                continue;
            }

            var count4 = (long)width * height * 4;
            if (chunkHeader + count4 > chunk.Length)
            {
                continue;
            }

            // libXcursor rejects a hotspot outside the bitmap; equality (the
            // right/bottom edge) is allowed, so only "greater than" skips.
            var xhot = BinaryPrimitives.ReadUInt32LittleEndian(chunk[24..]);
            var yhot = BinaryPrimitives.ReadUInt32LittleEndian(chunk[28..]);
            if (xhot > width || yhot > height)
            {
                continue;
            }

            if (totalPixels + ((long)width * height) > MaxTotalPixels)
            {
                break;
            }

            totalPixels += (long)width * height;
            var data = chunk[(int)chunkHeader..];
            var pixels = new uint[width * height];
            for (var pixel = 0; pixel < pixels.Length; pixel++)
            {
                pixels[pixel] = BinaryPrimitives.ReadUInt32LittleEndian(data[(pixel * 4)..]);
            }

            images.Add(new XCursorImage((int)subtype, (int)width, (int)height, (int)xhot, (int)yhot, pixels));
        }

        return images.Count > 0;
    }
}