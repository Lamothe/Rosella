namespace Rosella.Input.Tests;

/// <summary>Golden-byte decoding of <c>struct input_event</c> streams.</summary>
public class InputEventTests
{
    [Fact]
    public void Parse_ReadsLittleEndianFields()
    {
        // evdev key press: time 12.345678s, EV_KEY, KEY_A, pressed.
        var bytes = new byte[]
        {
            0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x4E, 0x46, 0x05, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0x1E, 0x00,
            0x01, 0x00, 0x00, 0x00,
        };

        var e = InputEvent.Parse(bytes);
        Assert.Equal(12, e.Sec);
        Assert.Equal(345678, e.Usec);
        Assert.Equal(1, e.Type);
        Assert.Equal(30, e.Code);
        Assert.Equal(1, e.Value);
        Assert.Equal(12345u, e.TimeMs);
    }

    [Fact]
    public void Parse_RejectsShortInput()
    {
        Assert.Throws<ArgumentException>(() => InputEvent.Parse(new byte[23]));
    }

    [Fact]
    public void ToBytes_RoundTrips()
    {
        var original = new InputEvent(5, 6, 2, 0, -3);
        Assert.Equal(original, InputEvent.Parse(original.ToBytes()));
    }

    [Fact]
    public void ToBytes_MatchesGoldenBytes()
    {
        var bytes = new InputEvent(1, 2, 3, 4, 5).ToBytes();
        Assert.Equal(
            new byte[]
            {
                0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x03, 0x00,
                0x04, 0x00,
                0x05, 0x00, 0x00, 0x00,
            },
            bytes);
    }

    [Fact]
    public void TimeMs_WrapsInsteadOfThrowing()
    {
        // 50 days after boot exceeds uint.MaxValue milliseconds. The
        // protocol timestamps wrap; the checked cast used to throw here and
        // take the reader thread down.
        var e = new InputEvent(50L * 24 * 60 * 60, 0, 0, 0, 0);
        Assert.Equal(unchecked((uint)(50L * 24 * 60 * 60 * 1000)), e.TimeMs);
    }
}
