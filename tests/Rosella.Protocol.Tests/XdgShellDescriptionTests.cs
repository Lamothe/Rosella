using Rosella.Protocol;

namespace Rosella.Protocol.Tests;

/// <summary>Pins the embedded xdg-shell definition to the protocol's wire reality.</summary>
public class XdgShellDescriptionTests
{
    [Fact]
    public void XdgShell_ParsesEveryInterface()
    {
        Assert.Equal("xdg_shell", ProtocolDescription.XdgShell.Name);
        Assert.Equal(7u, ProtocolDescription.XdgShell.FindInterface("xdg_wm_base")?.Version);
        Assert.Equal(7u, ProtocolDescription.XdgShell.FindInterface("xdg_positioner")?.Version);
        Assert.Equal(7u, ProtocolDescription.XdgShell.FindInterface("xdg_surface")?.Version);
        Assert.Equal(7u, ProtocolDescription.XdgShell.FindInterface("xdg_toplevel")?.Version);
        Assert.Equal(7u, ProtocolDescription.XdgShell.FindInterface("xdg_popup")?.Version);
    }

    [Fact]
    public void Combined_MergesCoreAndXdgShell()
    {
        Assert.NotNull(ProtocolDescription.Combined.FindInterface("wl_display"));
        Assert.NotNull(ProtocolDescription.Combined.FindInterface("xdg_wm_base"));
    }

    [Fact]
    public void Combine_RejectsDuplicateInterfaces()
    {
        Assert.Throws<InvalidOperationException>(() => ProtocolDescription.Combine(ProtocolDescription.Core, ProtocolDescription.Core));
    }

    [Fact]
    public void XdgSurface_OpcodePins()
    {
        var surface = ProtocolDescription.XdgShell.FindInterface("xdg_surface")!;
        Assert.Equal("n", surface.FindRequest(1)!.Signature); // get_toplevel
        Assert.Equal("n?oo", surface.FindRequest(2)!.Signature); // get_popup
        Assert.Equal("u", surface.FindEvent(0)!.Signature); // configure
    }

    [Fact]
    public void XdgWmBase_OpcodePins()
    {
        var wmBase = ProtocolDescription.XdgShell.FindInterface("xdg_wm_base")!;
        Assert.Equal("no", wmBase.FindRequest(2)!.Signature); // get_xdg_surface
    }

    [Fact]
    public void XdgToplevel_OpcodePins()
    {
        var toplevel = ProtocolDescription.XdgShell.FindInterface("xdg_toplevel")!;
        Assert.Equal("iia", toplevel.FindEvent(0)!.Signature); // configure
        Assert.Equal("close", toplevel.FindEvent(1)!.Name);
        Assert.Equal(4u, toplevel.FindEvent(2)!.Since); // configure_bounds
        Assert.Equal(5u, toplevel.FindEvent(3)!.Since); // wm_capabilities
    }

    [Fact]
    public void XdgPopup_OpcodePins()
    {
        var popup = ProtocolDescription.XdgShell.FindInterface("xdg_popup")!;
        Assert.Equal("iiii", popup.FindEvent(0)!.Signature); // configure
        Assert.Equal(3u, popup.FindRequest(2)!.Since); // reposition
    }
}
