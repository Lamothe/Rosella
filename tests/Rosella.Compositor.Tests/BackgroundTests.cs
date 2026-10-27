using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The desktop background: one calm dark gradient. The colour-bar test
/// pattern and its moving stripe are gone for good.
/// </summary>
public class BackgroundTests
{
    [Fact]
    public void Render_PaintsTheCalmGradient()
    {
        var width = 320;
        var height = 180;
        var pixels = new byte[width * height * 4];
        Background.Render(pixels, width, height, width * 4);

        static uint At(byte[] buffer, int stride, int x, int y) =>
            MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

        Assert.Equal(0xFF222C3A, At(pixels, width * 4, 0, 0));
        Assert.Equal(0xFF181E28, At(pixels, width * 4, 0, height - 1));
    }

    [Fact]
    public void Render_IsOpaqueAndVertical()
    {
        var width = 64;
        var height = 32;
        var pixels = new byte[width * height * 4];
        Background.Render(pixels, width, height, width * 4);

        for (var y = 0; y < height; y++)
        {
            uint rowColour = 0;
            for (var x = 0; x < width; x++)
            {
                var colour = MemoryMarshal.Read<uint>(pixels.AsSpan((y * width * 4) + (x * 4)));
                Assert.Equal(0xFFu, (colour >> 24) & 0xFFu);
                if (x == 0)
                {
                    rowColour = colour;
                }
                else
                {
                    Assert.Equal(rowColour, colour);
                }
            }
        }
    }
}
