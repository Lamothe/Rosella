namespace Rosella.Compositor.Tests;

/// <summary>
/// The XDG data-root search order: $XDG_DATA_HOME replaces ~/.local/share
/// as the user data root (the XDG spec says "if $XDG_DATA_HOME is either
/// not set or empty, a default equal to $HOME/.local/share should be used")
/// — it is never searched in addition to the default.
/// </summary>
public class DesktopLocatorTests
{
    [Fact]
    public void XdgDataHome_ReplacesTheDefaultUserDataRoot()
    {
        var dataHome = Path.Combine(Path.GetTempPath(), $"rosella-data-home-{Guid.NewGuid():N}");
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataHome);

            var roots = DesktopLocator.DefaultDataRoots();

            Assert.Equal(dataHome, roots[0]);
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.DoesNotContain(Path.Combine(home, ".local", "share"), roots);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }

    [Fact]
    public void WithoutXdgDataHome_TheDefaultUserDataRootIsUsed()
    {
        var previous = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", null);

            var roots = DesktopLocator.DefaultDataRoots();
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Assert.Contains(Path.Combine(home, ".local", "share"), roots);
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", previous);
        }
    }
}
