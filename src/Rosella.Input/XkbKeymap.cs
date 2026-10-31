using System.Runtime.InteropServices;
using System.Text;

namespace Rosella.Input;

/// <summary>
/// Generates the wl_keyboard keymap with xkbcommon (the standard keymap
/// library — the only one Rosella uses). Rosella is opinionated: exactly one
/// layout, US QWERTY, evdev scancodes, no configuration.
/// </summary>
public static partial class XkbKeymap
{
    private const string Library = "libxkbcommon.so.0";

    /// <summary>The XKB_V1 text format wl_keyboard.keymap requires.</summary>
    private const int FormatTextV1 = 1;

    /// <summary>The one keymap option: Caps Lock does nothing.</summary>
    private const string XkbOptions = "caps:none";

    /// <summary>Generates the US keymap string clients receive.</summary>
    public static string GenerateUsKeymap()
    {
        var context = xkb_context_new(0);
        if (context == 0)
        {
            throw new InvalidOperationException("xkb_context_new failed: libxkbcommon is not usable");
        }

        try
        {
            var layout = Marshal.StringToCoTaskMemUTF8("us");
            var options = Marshal.StringToCoTaskMemUTF8(XkbOptions);
            try
            {
                unsafe
                {
                    var names = stackalloc nint[5];
                    names[0] = 0; // rules: default (evdev)
                    names[1] = 0; // model
                    names[2] = layout;
                    names[3] = 0; // variant
                    names[4] = options; // caps:none — the Caps Lock key is dead

                    var keymap = xkb_keymap_new_from_names(context, (nint)names, 0);
                    if (keymap == 0)
                    {
                        throw new InvalidOperationException("xkb_keymap_new_from_names failed for layout 'us'");
                    }

                    try
                    {
                        var text = xkb_keymap_get_as_string(keymap, FormatTextV1);
                        if (text == 0)
                        {
                            throw new InvalidOperationException("xkb_keymap_get_as_string failed");
                        }

                        try
                        {
                            return Marshal.PtrToStringUTF8(text)
                                ?? throw new InvalidOperationException("the generated keymap was not UTF-8");
                        }
                        finally
                        {
                            Libc.free(text);
                        }
                    }
                    finally
                    {
                        xkb_keymap_unref(keymap);
                    }
                }
            }
            finally
            {
                Marshal.FreeCoTaskMem(layout);
                Marshal.FreeCoTaskMem(options);
            }
        }
        finally
        {
            xkb_context_unref(context);
        }
    }

    /// <summary>
    /// Creates the anonymous file wl_keyboard.keymap needs: the string with
    /// its NUL terminator, rewound to the start. The caller owns the fd.
    /// </summary>
    public static int CreateKeymapFile(string keymap)
    {
        var name = Encoding.UTF8.GetBytes("rosella-keymap\0");
        int fd;
        unsafe
        {
            fixed (byte* pointer = name)
            {
                fd = Libc.memfd_create((nint)pointer, 0);
            }
        }

        if (fd < 0)
        {
            throw new IOException($"memfd_create failed with errno {Marshal.GetLastPInvokeError()}");
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(keymap + "\0");
            unsafe
            {
                fixed (byte* pointer = bytes)
                {
                    var written = 0;
                    while (written < bytes.Length)
                    {
                        var result = Libc.write(fd, (nint)(pointer + written), (nuint)(bytes.Length - written));
                        if (result <= 0)
                        {
                            throw new IOException($"keymap write failed with errno {Marshal.GetLastPInvokeError()}");
                        }

                        written += (int)result;
                    }
                }
            }

            if (Libc.lseek(fd, 0, Libc.SeekSet) < 0)
            {
                throw new IOException($"keymap rewind failed with errno {Marshal.GetLastPInvokeError()}");
            }

            return fd;
        }
        catch
        {
            Libc.close(fd);
            throw;
        }
    }

    /// <summary>The keymap's size on the wire: the string plus its NUL.</summary>
    public static int KeymapFileSize(string keymap) => Encoding.UTF8.GetByteCount(keymap) + 1;

    /// <summary>Rewinds the keymap file to the start (the fd offset is shared with receivers).</summary>
    public static void Rewind(int fd)
    {
        if (Libc.lseek(fd, 0, Libc.SeekSet) < 0)
        {
            throw new IOException($"keymap rewind failed with errno {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>Closes a keymap file descriptor created here.</summary>
    public static void Close(int fd)
    {
        Libc.close(fd);
    }

    [LibraryImport(Library)]
    private static partial nint xkb_context_new(int flags);

    [LibraryImport(Library)]
    private static partial void xkb_context_unref(nint context);

    [LibraryImport(Library)]
    private static partial nint xkb_keymap_new_from_names(nint context, nint names, int flags);

    [LibraryImport(Library)]
    private static partial void xkb_keymap_unref(nint keymap);

    [LibraryImport(Library)]
    private static partial nint xkb_keymap_get_as_string(nint keymap, int format);
}
