namespace Rosella.Protocol.Tests;

public class FixedTests
{
    [Fact]
    public void FromInt_UsesEightFractionalBits()
    {
        Assert.Equal(768, Fixed.FromInt(3).RawValue);
        Assert.Equal(3.0, Fixed.FromInt(3).ToDouble());
    }

    [Fact]
    public void FromDouble_EncodesFractions()
    {
        Assert.Equal(128, Fixed.FromDouble(0.5).RawValue);
        Assert.Equal(-320, Fixed.FromDouble(-1.25).RawValue);
    }

    [Fact]
    public void FromDouble_RoundsHalvesAwayFromZero()
    {
        Assert.Equal(1, Fixed.FromDouble(1.0 / 512).RawValue);
        Assert.Equal(-1, Fixed.FromDouble(-1.0 / 512).RawValue);
    }

    [Fact]
    public void FromDouble_RejectsValuesOutside24BitRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed.FromDouble(1e9));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fixed.FromDouble(double.NaN));
    }

    [Fact]
    public void ToIntFloor_TruncatesTowardsNegativeInfinity()
    {
        Assert.Equal(1, Fixed.FromDouble(1.75).ToIntFloor());
        Assert.Equal(-2, Fixed.FromDouble(-1.5).ToIntFloor());
    }

    [Fact]
    public void FromRaw_RoundTripsExactly()
    {
        Assert.Equal(-321, Fixed.FromRaw(-321).RawValue);
        Assert.Equal(Fixed.FromRaw(256), Fixed.FromInt(1));
    }
}
