using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The session-routing lines in the control state. The sudo askpass
/// helper parses these — "vt N active" selects the display named by
/// "display X" — so the format is load-bearing.
/// </summary>
public sealed class SessionRouteTests
{
    [Fact]
    public void DisplayLine_NamesTheSocket()
    {
        Assert.Equal("display wayland-1", SessionRoute.DisplayLine("wayland-1"));
    }

    [Fact]
    public void DisplayLine_IsAbsentWithoutADisplay()
    {
        Assert.Null(SessionRoute.DisplayLine(""));
    }

    [Fact]
    public void VtLine_ReportsTheActiveSession()
    {
        Assert.Equal("vt 3 active", SessionRoute.VtLine((3, true)));
    }

    [Fact]
    public void VtLine_ReportsAnInactiveSession()
    {
        Assert.Equal("vt 2 inactive", SessionRoute.VtLine((2, false)));
    }

    [Fact]
    public void VtLine_IsAbsentWithoutAVt()
    {
        Assert.Null(SessionRoute.VtLine(null));
        Assert.Null(SessionRoute.VtLine((0, true)));
    }

    [Fact]
    public void Lines_MatchTheAskpassScriptContract()
    {
        // scripts/rosella-askpass consumes these lines: it strips the "vt "
        // and "display " prefixes and selects on a trailing " active". The
        // literals on both sides are the contract, so both are pinned here.
        var script = File.ReadAllText(RepoFiles.AskpassScript);
        Assert.Contains("s/^vt //p", script);
        Assert.Contains("s/^display //p", script);
        Assert.Contains("\" active\")", script);

        Assert.StartsWith("vt ", SessionRoute.VtLine((3, true)));
        Assert.EndsWith(" active", SessionRoute.VtLine((3, true)));
        Assert.StartsWith("display ", SessionRoute.DisplayLine("wayland-1"));
    }
}
