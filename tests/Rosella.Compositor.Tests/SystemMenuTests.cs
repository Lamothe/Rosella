using System.Runtime.InteropServices;
using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The apple system menu: three items wired to three actions through
/// ISystemActions (faked here), and the panel-close behaviour — Esc or a
/// click outside, no confirmation dialogs.
/// </summary>
public class SystemMenuTests
{
    [Fact]
    public void Items_AreLogOutRestartShutDownInOrder()
    {
        Assert.Equal(["Log Out", "Restart", "Shut Down"], SystemMenu.Items);
    }

    [Theory]
    [InlineData(0, "LogOut")]
    [InlineData(1, "Restart")]
    [InlineData(2, "ShutDown")]
    public void ClickOnAnItem_RunsItsActionAndCloses(int index, string action)
    {
        var actions = new FakeSystemActions();
        var menu = new SystemMenu(actions);
        menu.Open();

        var panel = menu.PanelRect(1920, 1080);
        var row = SystemMenu.RowRect(panel, index);
        var handled = menu.HandleClick(row.X + (row.W / 2), row.Y + (row.H / 2), 1920, 1080);

        Assert.True(handled);
        Assert.False(menu.IsOpen);
        Assert.Equal([action], actions.Calls);
    }

    [Fact]
    public void ClickOutside_ClosesWithoutActing()
    {
        var actions = new FakeSystemActions();
        var menu = new SystemMenu(actions);
        menu.Open();

        Assert.False(menu.HandleClick(0, 200, 1920, 1080));
        Assert.False(menu.IsOpen);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void ClickInsideButOffARow_IsConsumedWithoutActing()
    {
        var actions = new FakeSystemActions();
        var menu = new SystemMenu(actions);
        menu.Open();

        var panel = menu.PanelRect(1920, 1080);
        Assert.True(menu.HandleClick(panel.X + 2, panel.Y + 2, 1920, 1080));
        Assert.True(menu.IsOpen);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void Esc_ClosesThePanelAndIsConsumed()
    {
        var menu = new SystemMenu(new FakeSystemActions());
        menu.Open();

        Assert.True(menu.HandleKey(InputConstants.KeyEsc));
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void OtherKeys_AreNotConsumed()
    {
        var menu = new SystemMenu(new FakeSystemActions());
        menu.Open();

        Assert.False(menu.HandleKey(InputConstants.KeyEnter));
        Assert.True(menu.IsOpen);
    }

    [Fact]
    public void Panel_SitsAboveTheAppleButton()
    {
        var menu = new SystemMenu(new FakeSystemActions());
        var panel = menu.PanelRect(1920, 1080);

        Assert.Equal(8, panel.X);
        Assert.Equal(1080 - Shell.BarHeight, panel.Y + panel.H);
        Assert.True(panel.H > 0 && panel.W > 0);
    }

    [Fact]
    public void LogOut_IsTheGracefulQuit()
    {
        // The production action wires Log Out to the same cancellation the
        // Super+Esc binding uses; the menu must reach it exactly once.
        var quit = 0;
        var menu = new SystemMenu(new SystemActions(() => quit++));
        menu.Open();

        var panel = menu.PanelRect(1920, 1080);
        var row = SystemMenu.RowRect(panel, 0);
        menu.HandleClick(row.X + (row.W / 2), row.Y + (row.H / 2), 1920, 1080);

        Assert.Equal(1, quit);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void Draw_PaintsThePanelAndHighlightsOnlyTheHoveredRow()
    {
        var menu = new SystemMenu(new FakeSystemActions());
        menu.Open();

        // At scale 2 the logical panel maps onto physical pixels 1:2.
        var scale = 2;
        var width = 1920;
        var height = 1080;
        var stride = width * scale * 4;
        var panel = menu.PanelRect(width, height);
        var row0 = SystemMenu.RowRect(panel, 0);
        var row1 = SystemMenu.RowRect(panel, 1);

        var pixels = new byte[stride * height * scale];
        menu.Draw(pixels, width, height, stride, scale, (row1.X + 2, row1.Y + (row1.H / 2)));

        // The panel is painted...
        Assert.NotEqual(0u, PixelAt(pixels, stride, (panel.X + (panel.W / 2)) * scale, (panel.Y + 2) * scale));

        // ...the hovered row wears the selection fill...
        Assert.Equal(0xFFB8BEC8u, PixelAt(pixels, stride, (row1.X + 2) * scale, (row1.Y + (row1.H / 2)) * scale));

        // ...and the other row stays on the plain panel background.
        Assert.Equal(0xFF2E303Au, PixelAt(pixels, stride, (row0.X + 2) * scale, (row0.Y + (row0.H / 2)) * scale));

        // With no pointer the same row is plain panel background too.
        pixels = new byte[stride * height * scale];
        menu.Draw(pixels, width, height, stride, scale, null);
        Assert.Equal(0xFF2E303Au, PixelAt(pixels, stride, (row1.X + 2) * scale, (row1.Y + (row1.H / 2)) * scale));
    }

    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    private sealed class FakeSystemActions : ISystemActions
    {
        public List<string> Calls { get; } = [];

        public void LogOut() => Calls.Add("LogOut");

        public void Restart() => Calls.Add("Restart");

        public void ShutDown() => Calls.Add("ShutDown");
    }
}
