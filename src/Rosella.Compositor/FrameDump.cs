using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Rosella.Compositor;

/// <summary>
/// Writes raw scanout buffers to PPM (P6) files — the diagnostic ground
/// truth for "what is actually on screen" reports, read back pixel by
/// pixel rather than trusted from logs.
/// </summary>
internal static class FrameDump
{
    internal static void SaveScreenshot(byte[] pixels, int stride, DamageRect region, Action<byte[]> copy)
    {
        try
        {
            using var buffer = new MemoryStream();
            WritePng(buffer, pixels, stride, region);
            var png = buffer.ToArray();
            copy(png);

            var pictures = Environment.GetFolderPath(
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(pictures))
            {
                pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures");
            }

            var directory = Path.Combine(pictures, "Screenshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"Screenshot {DateTime.Now:yyyy-MM-dd HH-mm-ss.fffffff}.png");
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            {
                file.Write(png);
            }

            Console.WriteLine($"rosella: screenshot saved to {path}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rosella: could not save screenshot: {exception.Message}");
        }
    }

    private static void WritePng(Stream output, ReadOnlySpan<byte> pixels, int stride, DamageRect region)
    {
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        header.Clear();
        BinaryPrimitives.WriteInt32BigEndian(header, region.W);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], region.H);
        header[8] = 8;
        header[9] = 2; // RGB
        WriteChunk(output, "IHDR"u8, header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[region.W * 3 + 1];
            for (var y = region.Y; y < region.Y + region.H; y++)
            {
                var source = pixels.Slice(y * stride + region.X * 4, region.W * 4);
                for (var x = 0; x < region.W; x++)
                {
                    row[1 + x * 3] = source[x * 4 + 2];
                    row[2 + x * 3] = source[x * 4 + 1];
                    row[3 + x * 3] = source[x * 4];
                }

                zlib.Write(row);
            }
        }

        WriteChunk(output, "IDAT"u8, compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(output, "IEND"u8, []);
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
        output.Write(word);
        output.Write(type);
        output.Write(data);
        var crc = UpdateCrc(UpdateCrc(uint.MaxValue, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc);
        output.Write(word);
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            }
        }

        return crc;
    }

    internal static void WritePpm(string path, int width, int height, uint pitch, ReadOnlySpan<byte> pixels)
    {
        using var stream = File.Create(path);
        stream.Write(EncodePpm(width, height, pitch, pixels));
    }

    /// <summary>The same PPM as bytes, for the control socket's frame answers.</summary>
    internal static byte[] EncodePpm(int width, int height, uint pitch, ReadOnlySpan<byte> pixels)
    {
        using var stream = new MemoryStream();
        var header = Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        stream.Write(header);

        var row = new byte[width * 3];
        for (var y = 0; y < height; y++)
        {
            var source = pixels.Slice((int)(y * pitch), width * 4);
            for (var x = 0; x < width; x++)
            {
                // XRGB8888 little-endian: bytes B, G, R, X.
                row[x * 3] = source[(x * 4) + 2];
                row[(x * 3) + 1] = source[(x * 4) + 1];
                row[(x * 3) + 2] = source[x * 4];
            }

            stream.Write(row);
        }

        return stream.ToArray();
    }
}
