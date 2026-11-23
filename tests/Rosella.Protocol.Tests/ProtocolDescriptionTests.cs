namespace Rosella.Protocol.Tests;

public class ProtocolDescriptionTests
{
    [Fact]
    public void Core_LoadsEmbeddedWaylandXml()
    {
        Assert.Equal("wayland", ProtocolDescription.Core.Name);
        Assert.NotEmpty(ProtocolDescription.Core.Interfaces);
        Assert.Null(ProtocolDescription.Core.FindInterface("no_such_interface"));
    }

    [Fact]
    public void WlDisplay_RequestsAndEvents_HaveExpectedOpcodes()
    {
        var display = ProtocolDescription.Core.FindInterface("wl_display");

        Assert.NotNull(display);
        Assert.Equal(1u, display.Version);
        Assert.Equal("sync", display.FindRequest(0)?.Name);
        Assert.Equal("n", display.FindRequest(0)?.Signature);
        Assert.Equal("get_registry", display.FindRequest(1)?.Name);
        Assert.Equal("n", display.FindRequest(1)?.Signature);
        Assert.Null(display.FindRequest(2));

        // error.object_id is a plain object in wayland.xml 1.26 (not nullable).
        Assert.Equal("error", display.FindEvent(0)?.Name);
        Assert.Equal("ous", display.FindEvent(0)?.Signature);
        Assert.Equal("delete_id", display.FindEvent(1)?.Name);
        Assert.Equal("u", display.FindEvent(1)?.Signature);
    }

    [Fact]
    public void WlRegistry_Bind_ExpandsUntypedNewIdToUsun()
    {
        var registry = ProtocolDescription.Core.FindInterface("wl_registry");

        Assert.NotNull(registry);
        var bind = registry.FindRequest(0);
        Assert.NotNull(bind);
        Assert.Equal("bind", bind.Name);

        // Two XML arguments (name, id), four wire fields: the untyped new_id
        // expands to interface string + version + id, in that order.
        Assert.Equal(2, bind.Arguments.Count);
        Assert.Equal(4, bind.WireSignature.Length);
        Assert.Equal("usun", bind.Signature);
        Assert.Equal(
            new[] { ArgumentKind.UInt32, ArgumentKind.String, ArgumentKind.UInt32, ArgumentKind.NewId },
            bind.WireSignature.Select(t => t.Kind));

        Assert.Equal("global", registry.FindEvent(0)?.Name);
        Assert.Equal("usu", registry.FindEvent(0)?.Signature);
        Assert.Equal("global_remove", registry.FindEvent(1)?.Name);
        Assert.Equal("u", registry.FindEvent(1)?.Signature);
    }

    [Fact]
    public void WlCallback_Done_Event()
    {
        var callback = ProtocolDescription.Core.FindInterface("wl_callback");

        Assert.NotNull(callback);
        Assert.Equal("done", callback.FindEvent(0)?.Name);
        Assert.Equal("u", callback.FindEvent(0)?.Signature);
    }

    [Fact]
    public void Core_Interfaces_HaveExpectedVersions()
    {
        // These pin the embedded wayland.xml (1.26) versions.
        Assert.Equal(7u, ProtocolDescription.Core.FindInterface("wl_compositor")?.Version);
        Assert.Equal(3u, ProtocolDescription.Core.FindInterface("wl_shm")?.Version);
        Assert.Equal(11u, ProtocolDescription.Core.FindInterface("wl_seat")?.Version);
        Assert.Equal(1u, ProtocolDescription.Core.FindInterface("wl_subcompositor")?.Version);
        Assert.Equal(4u, ProtocolDescription.Core.FindInterface("wl_output")?.Version);
    }

    [Fact]
    public void Messages_CarrySinceVersions()
    {
        var seat = ProtocolDescription.Core.FindInterface("wl_seat");
        Assert.Equal(1u, seat?.FindEvent(0)?.Since); // capabilities
        Assert.Equal(2u, seat?.FindEvent(1)?.Since); // name

        var output = ProtocolDescription.Core.FindInterface("wl_output");
        Assert.Equal(4u, output?.Events.First(e => e.Name == "name").Since);
        Assert.Equal(1u, ProtocolDescription.Core.FindInterface("wl_display")?.FindRequest(0)?.Since);
    }

    [Fact]
    public void Load_RejectsInvalidSince()
    {
        const string template = """
            <protocol name="test">
              <interface name="thing" version="3">
                <request name="poke" {SINCE}>
                  <arg name="x" type="uint"/>
                </request>
              </interface>
            </protocol>
            """;

        Assert.Throws<FormatException>(() => LoadXml(template.Replace("{SINCE}", "since=\"0\"")));
        Assert.Throws<FormatException>(() => LoadXml(template.Replace("{SINCE}", "since=\"abc\"")));

        var valid = LoadXml(template.Replace("{SINCE}", "since=\"3\""));
        Assert.Equal(3u, valid.FindInterface("thing")?.FindRequest(0)?.Since);
    }

    private static ProtocolDescription LoadXml(string xml)
    {
        return ProtocolDescription.Load(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml)));
    }

    [Fact]
    public void TypedNewId_DoesNotExpand()
    {
        var compositor = ProtocolDescription.Core.FindInterface("wl_compositor");

        Assert.NotNull(compositor);
        var createSurface = compositor.FindRequest(0);
        Assert.NotNull(createSurface);
        Assert.Equal("create_surface", createSurface.Name);
        Assert.Equal("n", createSurface.Signature);
        Assert.Equal("wl_surface", createSurface.Arguments[0].Interface);
    }
}
