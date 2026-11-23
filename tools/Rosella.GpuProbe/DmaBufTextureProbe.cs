using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// Allocates a renderable buffer in the driver's preferred layout, exports it,
/// then reimports it as a texture beside the probe's uploaded scene textures.
/// This uses only the render node and does not acquire DRM master.
/// </summary>
internal static unsafe class DmaBufTextureProbe
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int BufferWidth = 256;
    private const int BufferHeight = 128;
    private const ulong ModifierInvalid = 0x00FFFFFFFFFFFFFF;

    internal static bool Run(TextWriter output, GpuContext context, string? cardPath = null)
    {
        if (context.CreateImage is null || context.DestroyImage is null)
        {
            output.WriteLine("DMA-BUF EGL image entry points unavailable.");
            return false;
        }

        using var scene = new SceneRenderer(context, Width, Height);
        if (cardPath is not null)
        {
            return CheckFramebuffer(output, context, scene, cardPath);
        }

        var target = scene.CreateColourTexture(Width, Height);
        try
        {
            return CompositeScene(output, context, scene, target);
        }
        finally
        {
            SceneRenderer.DeleteTexture(target);
        }
    }

    private static bool CompositeScene(TextWriter output, GpuContext context, SceneRenderer scene, uint target)
    {
        scene.Attach(target);
        scene.Draw();
        var ordinaryWindow = ReadPixel(512, Height - 500);
        var wallpaper = ReadPixel(32, Height - 32);
        var success = false;
        foreach (var (name, format) in new[] { ("XR24", Gbm.FormatXrgb8888), ("AR24", Gbm.FormatArgb8888) })
        {
            success |= TryFormat(output, context, scene, name, format, target, ordinaryWindow, wallpaper);
        }

        return success;
    }

    /// <summary>Checks whether KMS can register a GPU render target without committing it to a display.</summary>
    private static bool CheckFramebuffer(TextWriter output, GpuContext context, SceneRenderer scene, string cardPath)
    {
        var bo = Gbm.BoCreate(context.GbmDevice, Width, Height, Gbm.FormatXrgb8888, Gbm.BoUseRendering | Gbm.BoUseScanout);
        if (bo == 0)
        {
            output.WriteLine("KMS: GBM could not allocate a renderable scanout buffer.");
            return false;
        }

        try
        {
            var modifier = Gbm.BoGetModifier(bo);
            if (Gbm.BoGetPlaneCount(bo) != 1 || modifier == ModifierInvalid)
            {
                output.WriteLine("KMS: this check needs one plane and an explicit modifier.");
                return false;
            }

            var exported = Gbm.BoGetFd(bo);
            if (exported < 0)
            {
                output.WriteLine("KMS: GBM could not export the scanout buffer.");
                return false;
            }

            try
            {
                var stride = Gbm.BoGetStride(bo);
                var offset = Gbm.BoGetOffset(bo, 0);
                var target = Import(context, scene, ImageAttributes(Width, Height, Gbm.FormatXrgb8888,
                    exported, stride, offset, modifier));
                try
                {
                    if (!CompositeScene(output, context, scene, target))
                    {
                        return false;
                    }

                    Gles.Finish();
                    return RegisterFramebuffer(output, cardPath, Width, Height, exported, stride, offset, modifier);
                }
                finally
                {
                    SceneRenderer.DeleteTexture(target);
                }
            }
            finally
            {
                Libc.Close(exported);
            }
        }
        finally
        {
            Gbm.BoDestroy(bo);
        }
    }

    private static bool RegisterFramebuffer(TextWriter output, string cardPath, uint width, uint height, int exported,
        uint stride, uint offset, ulong modifier)
    {
        var card = Libc.Open(cardPath, Libc.O_RdWr | Libc.OCloExec);
        if (card < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            output.WriteLine($"KMS: open {cardPath} failed: {Libc.ErrorName(errno)} ({errno})");
            return false;
        }

        try
        {
            if (LibDrm.PrimeFdToHandle(card, exported, out var handle) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                output.WriteLine($"KMS: PRIME import on {cardPath} failed: {Libc.ErrorName(errno)} ({errno})");
                return false;
            }

            try
            {
                uint* handles = stackalloc uint[4] { handle, 0, 0, 0 };
                uint* pitches = stackalloc uint[4] { stride, 0, 0, 0 };
                uint* offsets = stackalloc uint[4] { offset, 0, 0, 0 };
                ulong* modifiers = stackalloc ulong[4] { modifier, 0, 0, 0 };
                var result = LibDrm.ModeAddFb2WithModifiers(card, width, height, Gbm.FormatXrgb8888, handles,
                    pitches, offsets, modifiers, out var framebuffer, LibDrm.ModeFbModifiers);
                output.WriteLine($"KMS: {cardPath} {width}x{height} XR24 pitch {stride}, modifier 0x{modifier:X16}, AddFB2WithModifiers {DumbBuffer.Describe(result)}");
                if (result != 0)
                {
                    return false;
                }

                LibDrm.ModeRmFb(card, framebuffer);
                return true;
            }
            finally
            {
                LibDrm.CloseBufferHandle(card, handle);
            }
        }
        finally
        {
            Libc.Close(card);
        }
    }

    private static bool TryFormat(TextWriter output, GpuContext context, SceneRenderer scene, string name,
        uint format, uint target, uint ordinaryWindow, uint wallpaper)
    {
        var bo = Gbm.BoCreate(context.GbmDevice, BufferWidth, BufferHeight, format, Gbm.BoUseRendering);
        if (bo == 0)
        {
            output.WriteLine($"{name}: driver-selected GBM allocation failed.");
            return false;
        }

        try
        {
            if (Gbm.BoGetPlaneCount(bo) != 1)
            {
                output.WriteLine($"{name}: driver allocated multiple planes; this probe checks one plane.");
                return false;
            }

            var fd = Gbm.BoGetFd(bo);
            if (fd < 0)
            {
                output.WriteLine($"{name}: GBM could not export a DMA-BUF descriptor.");
                return false;
            }

            try
            {
                var modifier = Gbm.BoGetModifier(bo);
                var pitch = Gbm.BoGetStride(bo);
                var attributes = ImageAttributes(BufferWidth, BufferHeight, format, fd, pitch, Gbm.BoGetOffset(bo, 0), modifier);

                output.WriteLine($"{name}: pitch {pitch}, modifier 0x{modifier:X16}");
                var producer = Import(context, scene, attributes);
                try
                {
                    scene.Attach(producer);
                    scene.ClearAttached(0.25f, 0.5f, 0.75f);
                    Gles.Finish();
                }
                finally
                {
                    scene.Attach(target);
                    SceneRenderer.DeleteTexture(producer);
                }

                var consumer = Import(context, scene, attributes);
                try
                {
                    scene.Draw(consumer);
                    var importedWindow = ReadPixel(512, Height - 500);
                    var untouched = ReadPixel(32, Height - 32);
                    // The scene shader tints window RGB by (34, 36, 42).
                    var passed = ordinaryWindow != importedWindow &&
                        Near(importedWindow, 9, 18, 32) && wallpaper == untouched;
                    output.WriteLine($"{name}: mixed composite {(passed ? "OK" : "FAILED")}, window RGBA 0x{importedWindow:X8}, wallpaper {(wallpaper == untouched ? "unchanged" : "changed")}");
                    return passed;
                }
                finally
                {
                    SceneRenderer.DeleteTexture(consumer);
                }
            }
            finally
            {
                Libc.Close(fd);
            }
        }
        catch (ProbeException exception)
        {
            output.WriteLine($"{name}: {exception.Message}");
            return false;
        }
        finally
        {
            Gbm.BoDestroy(bo);
        }
    }

    private static int[] ImageAttributes(int width, int height, uint format, int fd, uint stride, uint offset, ulong modifier)
    {
        int[] attributes =
        [
            Egl.Width, width,
            Egl.Height, height,
            Egl.LinuxDrmFourccExt, unchecked((int)format),
            Egl.DmaBufPlane0FdExt, fd,
            Egl.DmaBufPlane0OffsetExt, checked((int)offset),
            Egl.DmaBufPlane0PitchExt, checked((int)stride),
        ];

        return modifier == ModifierInvalid
            ? [.. attributes, Egl.None]
            :
            [
                .. attributes,
                Egl.DmaBufPlane0ModifierLoExt, unchecked((int)modifier),
                Egl.DmaBufPlane0ModifierHiExt, unchecked((int)(modifier >> 32)),
                Egl.None,
            ];
    }

    private static uint Import(GpuContext context, SceneRenderer scene, int[] attributes)
    {
        nint image;
        fixed (int* pointer = attributes)
        {
            image = context.CreateImage!(context.Display, 0, Egl.LinuxDmaBufExt, 0, pointer);
        }

        if (image == 0)
        {
            throw new ProbeException($"EGL DMA-BUF import failed: {Egl.Describe(Egl.GetError())}");
        }

        try
        {
            return scene.CreateImageTexture(image);
        }
        finally
        {
            context.DestroyImage!(context.Display, image);
        }
    }

    private static uint ReadPixel(int x, int y)
    {
        uint pixel = 0;
        Gles.ReadPixels(x, y, 1, 1, Gles.Rgba, Gles.UnsignedByte, (nint)(&pixel));
        var error = Gles.GetError();
        if (error != Gles.NoError)
        {
            throw new ProbeException($"composite readback failed: 0x{error:X4}");
        }

        return pixel;
    }

    private static bool Near(uint rgba, int red, int green, int blue) =>
        Math.Abs((int)(rgba & 255) - red) <= 1 &&
        Math.Abs((int)((rgba >> 8) & 255) - green) <= 1 &&
        Math.Abs((int)((rgba >> 16) & 255) - blue) <= 1 &&
        (rgba >> 24) == 255;
}
