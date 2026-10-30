using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The SVG path reader: the grammar subset the shell's silhouettes use,
/// flattened and hit-tested by even-odd parity.
/// </summary>
public sealed class SvgPathTests
{
    [Fact]
    public void Parse_ReadsMovetoLinetoClosepath()
    {
        var path = SvgPath.Parse("M0 0L10 0L10 10L0 10Z");
        Assert.True(path.Inside(5, 5));
        Assert.False(path.Inside(15, 5));
        Assert.False(path.Inside(5, -1));
    }

    [Fact]
    public void Parse_ImplicitRepetitionOfMovetoIsLineto()
    {
        var path = SvgPath.Parse("M0 0 10 0 10 10 0 10Z");
        Assert.True(path.Inside(5, 5));
        Assert.False(path.Inside(15, 5));
    }

    [Fact]
    public void Parse_ReadsRelativeCurvetoWithImplicitRepetition()
    {
        // One curve command, two segments: the second repeats the command.
        var path = SvgPath.Parse("M0 0c10 0 10 10 0 10c10 0 10 10 0 10L-10 20L-10 0Z");
        Assert.True(path.Inside(-5, 15));
        Assert.False(path.Inside(15, 15));
    }

    [Fact]
    public void Parse_ReadsHorizontalAndVerticalLineto()
    {
        var path = SvgPath.Parse("M0 0H10V10H0Z");
        Assert.True(path.Inside(5, 5));
        Assert.False(path.Inside(12, 5));
    }

    [Fact]
    public void Parse_AcceptsScientificNotationAndRunTogetherNumbers()
    {
        var path = SvgPath.Parse("M0 0L1e1 0L10 10 0 10Z");
        Assert.True(path.Inside(5, 5));
    }

    [Fact]
    public void Parse_RejectsParametersBeforeAnyCommand()
    {
        Assert.Throws<FormatException>(() => SvgPath.Parse("5 5L10 10"));
    }

    [Fact]
    public void Parse_RejectsUnknownCommands()
    {
        Assert.Throws<FormatException>(() => SvgPath.Parse("M0 0Q1 1 2 2"));
    }

    [Fact]
    public void Bounds_CoversEverySubpath()
    {
        var path = SvgPath.Parse("M2 3L8 3L8 9L2 9Z M20 30L24 30L24 34L20 34Z");
        var (minX, minY, maxX, maxY) = path.Bounds;
        Assert.Equal(2, minX, 6);
        Assert.Equal(3, minY, 6);
        Assert.Equal(24, maxX, 6);
        Assert.Equal(34, maxY, 6);
    }

    [Fact]
    public void Inside_UsesEvenOddAcrossSubpaths()
    {
        // A square with a square hole punched by a nested subpath.
        var path = SvgPath.Parse("M0 0L10 0L10 10L0 10Z M3 3L7 3L7 7L3 7Z");
        Assert.True(path.Inside(1, 1));
        Assert.False(path.Inside(5, 5));
        Assert.True(path.Inside(8, 8));
    }

    [Fact]
    public void Parse_ReadsLeadingDotFractions()
    {
        // ".5" has no integer part; the reader must not stall or drop it.
        var path = SvgPath.Parse("M.5 .5L10.5.5L10.5 10L.5 10Z");
        Assert.True(path.Inside(5, 5));
        Assert.False(path.Inside(11, 5));
    }

    [Fact]
    public void Parse_SplitsNumbersGluedAtTheDecimalPoint()
    {
        // "10.5.5" is two numbers: 10.5 then .5.
        var path = SvgPath.Parse("M0 0L10.5.5L10.5 10L0 10Z");
        Assert.True(path.Inside(5, 5));
        Assert.False(path.Inside(11, 5));
    }

    [Fact]
    public void Parse_SplitsRunTogetherSignedNumbers()
    {
        // "10 10-2 10": a minus ends the previous number and starts the next.
        var path = SvgPath.Parse("M0 0L10 0L10 10-2 10Z");
        Assert.True(path.Inside(5, 5));
        Assert.False(path.Inside(11, 5));
    }

    [Fact]
    public async Task Parse_RejectsParametersAfterClosepath()
    {
        // Closepath takes no parameters and has no implicit repetition; this
        // input once looped forever, re-running Z without consuming them.
        // Bounded: against the old parser this must FAIL, not hang the run.
        var parse = Task.Run(() => SvgPath.Parse("M0 0L10 0L10 10Z 5 5"));
        var finished = await Task.WhenAny(parse, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(parse, finished);
        await Assert.ThrowsAsync<FormatException>(() => parse);
    }

    [Fact]
    public void Parse_AcceptsWhitespaceOnlyAsAnEmptyPath()
    {
        var path = SvgPath.Parse("  \t ");
        Assert.False(path.Inside(5, 5));
        Assert.Equal((0, 0, 0, 0), path.Bounds);
    }
}
