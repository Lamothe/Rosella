namespace Rosella.Compositor.Tests;

public class BlitterTests
{
    [Fact]
    public void Copy_CopiesAtPosition()
    {
        var target = new byte[16 * 16 * 4];
        var source = new byte[4 * 4 * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 0x11;
        }

        Blitter.Copy(target, 16, 16, 16 * 4, source, 4, 4, x: 8, y: 8);

        Assert.Equal(0x11, target[(8 * 16 * 4) + (8 * 4)]);
        Assert.Equal(0x11, target[(11 * 16 * 4) + (11 * 4)]);
        Assert.Equal(0x00, target[(7 * 16 * 4) + (7 * 4)]);
    }

    [Fact]
    public void Copy_ClipsAtTargetEdges()
    {
        var target = new byte[8 * 8 * 4];
        var source = new byte[4 * 4 * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 0x22;
        }

        // Partially off the left and bottom edges.
        Blitter.Copy(target, 8, 8, 8 * 4, source, 4, 4, x: -2, y: 6);

        Assert.Equal(0x22, target[(6 * 8 * 4) + 0]); // column 0 came from source column 2
        Assert.Equal(0x22, target[(7 * 8 * 4) + (1 * 4)]);
        Assert.Equal(0x00, target[(5 * 8 * 4)]); // above the copy
    }

    [Fact]
    public void Copy_OffscreenDoesNothing()
    {
        var target = new byte[8 * 8 * 4];
        var source = new byte[4 * 4 * 4];
        source[0] = 0x33;

        Blitter.Copy(target, 8, 8, 8 * 4, source, 4, 4, x: 20, y: 20);
        Blitter.Copy(target, 8, 8, 8 * 4, source, 4, 4, x: -20, y: -20);
        Assert.All(target, value => Assert.Equal(0x00, value));
    }

    [Fact]
    public void Copy_LeavesTargetStridePaddingUntouched()
    {
        var stride = (4 * 4) + 8;
        var target = new byte[stride * 4];
        Array.Fill(target, (byte)0xCC);
        var source = new byte[4 * 4 * 4];

        Blitter.Copy(target, 4, 4, stride, source, 4, 4, x: 0, y: 0);

        for (var y = 0; y < 4; y++)
        {
            for (var i = 4 * 4; i < stride; i++)
            {
                Assert.Equal(0xCC, target[(y * stride) + i]);
            }
        }
    }
}
