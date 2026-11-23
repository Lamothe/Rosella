using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

public sealed class FrameDumpTests
{
    [Fact]
    public void WritesPpmWithRgbByteOrder()
    {
        // 2x2 XRGB buffer: little-endian pixels are B, G, R, X bytes.
        var pixels = new byte[2 * 2 * 4];
        pixels[0] = 0x11; // B
        pixels[1] = 0x22; // G
        pixels[2] = 0x33; // R
        pixels[3] = 0x00;

        var path = Path.Combine(Path.GetTempPath(), $"rosella-frame-{Guid.NewGuid():N}.ppm");
        try
        {
            FrameDump.WritePpm(path, 2, 2, 8, pixels);

            var bytes = File.ReadAllBytes(path);
            var header = System.Text.Encoding.ASCII.GetBytes("P6\n2 2\n255\n");
            Assert.Equal(header, bytes.AsSpan(0, header.Length).ToArray());
            Assert.Equal(0x33, bytes[header.Length]); // R first in PPM
            Assert.Equal(0x22, bytes[header.Length + 1]);
            Assert.Equal(0x11, bytes[header.Length + 2]);
            Assert.Equal(header.Length + (2 * 2 * 3), bytes.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
