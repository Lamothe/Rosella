using System.Text;

/// <summary>
/// Writes raw scanout buffers to PPM (P6) files — the diagnostic ground
/// truth for "what is actually on screen" reports, read back pixel by
/// pixel rather than trusted from logs.
/// </summary>
internal static class FrameDump
{
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
