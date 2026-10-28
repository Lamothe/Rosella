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
        Assert.True(AppleIcon.IsSet(7, 13));

        // Stem fills the notch above the body.
        Assert.True(AppleIcon.IsSet(9, 2));

        // Leaf sticks out top-right.
        Assert.True(AppleIcon.IsSet(14, 1));

        // The bite empties the upper-right flank (the body reaches x17
        // lower down but only x14 around y8).
        Assert.False(AppleIcon.IsSet(16, 8));
        Assert.True(AppleIcon.IsSet(17, 13));

        // Outside the art.
        Assert.False(AppleIcon.IsSet(0, 0));
    }
}
