using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// A decoded icon in the compositor's pixel format: one premultiplied
/// 0xAARRGGBB uint per pixel, the same layout the ARGB blit path expects.
/// </summary>
internal sealed record IconImage(uint[] Pixels, int Width, int Height);

/// <summary>
/// Icon decoding through gdk-pixbuf — the shell's only native dependency
/// beyond libc. GdkPixbuf hands back straight RGBA8 rows; they are
/// premultiplied here so the existing source-over blit can composite them.
/// A missing library (or a missing image loader, e.g. SVG on a bare host)
/// simply produces no icon: the taskbar draws its fallback tile.
/// </summary>
internal static class IconLoader
{
    private static bool _missingLogged;

    /// <summary>False when libgdk_pixbuf cannot be loaded on this host.</summary>
    internal static bool Available { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            return NativeLibrary.TryLoad("libgdk_pixbuf-2.0.so.0", out var handle) && handle != IntPtr.Zero;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes the image at exactly (size, size), or null on any failure
    /// (missing library, unsupported format, unreadable file).
    /// </summary>
    internal static IconImage? Load(string path, int size)
    {
        if (size <= 0)
        {
            return null;
        }

        if (!Available)
        {
            LogMissing();
            return null;
        }

        try
        {
            return LoadCore(path, size);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            LogMissing();
            return null;
        }
        catch (Exception exception)
        {
            Console.WriteLine($"icon: {path}: {exception.Message}");
            return null;
        }
    }

    private static IconImage? LoadCore(string path, int size)
    {
        var pixbuf = Native.gdk_pixbuf_new_from_file_at_scale(path, size, size, 0, out var error);
        if (pixbuf == IntPtr.Zero)
        {
            if (error != IntPtr.Zero)
            {
                Native.g_error_free(error);
            }

            return null;
        }

        try
        {
            var width = Native.gdk_pixbuf_get_width(pixbuf);
            var height = Native.gdk_pixbuf_get_height(pixbuf);
            var rowstride = Native.gdk_pixbuf_get_rowstride(pixbuf);
            var channels = Native.gdk_pixbuf_get_n_channels(pixbuf);
            var hasAlpha = Native.gdk_pixbuf_get_has_alpha(pixbuf) != 0;
            if (width <= 0 || height <= 0 || rowstride < width * channels || channels < 3)
            {
                return null;
            }

            // gdk-pixbuf does NOT pad the last row to rowstride: only
            // (height - 1) * rowstride + width * channels bytes are
            // readable (the contract of gdk_pixbuf_get_pixels), so a
            // rowstride * height copy over-reads the pixel buffer's last
            // row on any padded pixbuf. Copy exactly the safe extent.
            var raw = new byte[PixelByteCount(rowstride, width, height, channels)];
            CopyRawPixels(Native.gdk_pixbuf_get_pixels(pixbuf), raw, rowstride, width, height, channels);

            // Straight RGBA (or RGB) in, premultiplied ARGB uint out.
            var pixels = new uint[width * height];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var offset = (y * rowstride) + (x * channels);
                    uint red = raw[offset];
                    uint green = raw[offset + 1];
                    uint blue = raw[offset + 2];
                    uint alpha = hasAlpha ? raw[offset + 3] : 255u;

                    // Premultiply with rounding — (c * a + 127) / 255 — so
                    // faint colours do not truncate towards zero.
                    pixels[(y * width) + x] = (alpha << 24)
                        | ((((red * alpha) + 127) / 255) << 16)
                        | ((((green * alpha) + 127) / 255) << 8)
                        | (((blue * alpha) + 127) / 255);
                }
            }

            return new IconImage(pixels, width, height);
        }
        finally
        {
            Native.g_object_unref(pixbuf);
        }
    }

    /// <summary>
    /// The documented readable extent of a pixbuf's pixel buffer. Rows may
    /// carry padding (rowstride &gt; width * channels — e.g. any 3-channel
    /// pixbuf 3 wide has rowstride 12), but the LAST row is not padded:
    /// only (height - 1) * rowstride + width * channels bytes exist. A
    /// rowstride * height copy — the classic mistake — over-reads the heap
    /// by the last row's padding.
    /// </summary>
    internal static int PixelByteCount(int rowstride, int width, int height, int channels) =>
        ((height - 1) * rowstride) + (width * channels);

    /// <summary>
    /// Copies the safe extent of the pixbuf's raw pixel bytes into
    /// <paramref name="dest"/>, which must hold at least
    /// <see cref="PixelByteCount"/> bytes. Nothing past that extent is
    /// ever read.
    /// </summary>
    internal static void CopyRawPixels(IntPtr pixels, byte[] dest, int rowstride, int width, int height, int channels) =>
        Marshal.Copy(pixels, dest, 0, PixelByteCount(rowstride, width, height, channels));

    private static void LogMissing()
    {
        if (_missingLogged)
        {
            return;
        }

        _missingLogged = true;
        Console.WriteLine("icon: libgdk_pixbuf-2.0.so.0 unavailable; taskbar icons fall back to text tiles");
    }

    /// <summary>The gdk-pixbuf slice of the C ABI (g_object/g_error ride its dependency chain).</summary>
    private static class Native
    {
        private const string Library = "libgdk_pixbuf-2.0.so.0";

        [DllImport(Library, EntryPoint = "gdk_pixbuf_new_from_file_at_scale")]
        internal static extern IntPtr gdk_pixbuf_new_from_file_at_scale(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
            int width,
            int height,
            int preserveAspectRatio,
            out IntPtr error);

        [DllImport(Library)]
        internal static extern IntPtr gdk_pixbuf_get_pixels(IntPtr pixbuf);

        [DllImport(Library)]
        internal static extern int gdk_pixbuf_get_width(IntPtr pixbuf);

        [DllImport(Library)]
        internal static extern int gdk_pixbuf_get_height(IntPtr pixbuf);

        [DllImport(Library)]
        internal static extern int gdk_pixbuf_get_rowstride(IntPtr pixbuf);

        [DllImport(Library)]
        internal static extern int gdk_pixbuf_get_has_alpha(IntPtr pixbuf);

        [DllImport(Library)]
        internal static extern int gdk_pixbuf_get_n_channels(IntPtr pixbuf);

        [DllImport(Library)]
        internal static extern void g_object_unref(IntPtr obj);

        [DllImport(Library)]
        internal static extern void g_error_free(IntPtr error);
    }
}
