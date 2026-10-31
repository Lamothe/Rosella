using System.Text;

namespace Rosella.Input.Tests;

/// <summary>The one keymap: generated with xkbcommon, US QWERTY, XKB_V1.</summary>
public class XkbKeymapTests
{
    [Fact]
    public void GenerateUsKeymap_ProducesAValidXkbKeymap()
    {
        var keymap = XkbKeymap.GenerateUsKeymap();

        Assert.StartsWith("xkb_keymap", keymap);
        Assert.Contains("xkb_keycodes", keymap);
        Assert.Contains("xkb_types", keymap);
        Assert.Contains("xkb_compatibility", keymap);
        Assert.Contains("xkb_symbols", keymap);
        // The one opinionated option: Caps Lock is dead. The compiled keymap
        // bakes it in: <CAPS> is a one-level key with no Lock symbol and no
        // Lock anywhere in the symbols section.
        var caps = keymap[keymap.IndexOf("key <CAPS>", StringComparison.Ordinal)..];
        caps = caps[..caps.IndexOf("};", StringComparison.Ordinal)];
        Assert.Contains("ONE_LEVEL", caps);
        Assert.DoesNotContain("Lock", caps);
    }

    [Fact]
    public void KeymapFileSize_IncludesTheNullTerminator()
    {
        Assert.Equal(3, XkbKeymap.KeymapFileSize("ab"));
    }

    [Fact]
    public void CreateKeymapFile_WritesContentAndNullTerminator()
    {
        var fd = XkbKeymap.CreateKeymapFile("hello");
        try
        {
            Assert.True(fd >= 0);

            // /proc/self/fd opens a fresh description at offset 0.
            var bytes = File.ReadAllBytes($"/proc/self/fd/{fd}");
            Assert.Equal("hello\0", System.Text.Encoding.UTF8.GetString(bytes));

            // Rewind makes a re-read start over (callers rewind before send).
            XkbKeymap.Rewind(fd);
            Assert.Equal(bytes, File.ReadAllBytes($"/proc/self/fd/{fd}"));
        }
        finally
        {
            XkbKeymap.Close(fd);
        }
    }

    [Fact]
    public void GenerateUsKeymap_SizeIncludesTheNullTerminator()
    {
        var keymap = XkbKeymap.GenerateUsKeymap();
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(keymap) + 1, XkbKeymap.KeymapFileSize(keymap));
    }
}
