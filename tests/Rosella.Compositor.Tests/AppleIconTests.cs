using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

public sealed class AppleIconTests
{
    [Fact]
    public void EveryRowIsTwentyWide()
    {
        for (var y = 0; y < AppleIcon.Height; y++)
        {
            for (var x = 0; x < AppleIcon.Width; x++)
            {
                _ = AppleIcon.IsSet(x, y);
            }
        }
    }

    [Fact]
    public void ShapeHasBodyStemLeafAndBite()
    {
        // Body centre is solid.
        Assert.True(AppleIcon.IsSet(9, 13));

        // Stem fills the notch above the body.
        Assert.True(AppleIcon.IsSet(8, 2));

        // Leaf sticks out top-right.
        Assert.True(AppleIcon.IsSet(13, 1));

        // The bite empties the upper-right flank (the body reaches x16
        // lower down but only x12 around y8).
        Assert.False(AppleIcon.IsSet(16, 8));
        Assert.True(AppleIcon.IsSet(16, 13));

        // Outside the art.
        Assert.False(AppleIcon.IsSet(0, 0));
    }
}
