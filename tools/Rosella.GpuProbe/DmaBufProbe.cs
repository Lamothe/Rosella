using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// The dma-buf question, answered with ioctl results and nothing else: can a
/// buffer exported from the i915 render node be turned into a framebuffer on
/// the simpledrm card node? No page flip, no modeset, no master.
/// <para>
/// The dumb-buffer control matters: it proves the AddFB2 call itself works on
/// card0, so a failure on the dma-buf row is about the buffer, not the probe.
/// </para>
/// </summary>
internal static unsafe class DmaBufProbe
{
    internal sealed record Row(string Target, string Source, string Call, string Result);

    internal static List<Row> Run(int card0Fd, int card1Fd, string card0Path, string card1Path, GpuContext context, uint width, uint height)
    {
        var rows = new List<Row>();

        // A linear, renderable bo on i915 — the shape a GPU-rendered scanout
        // buffer would have.
        var bo = Gbm.BoCreate(context.GbmDevice, width, height, Gbm.FormatXrgb8888, Gbm.BoUseLinear | Gbm.BoUseRendering);
        if (bo == 0)
        {
            rows.Add(new Row("renderD128", "gbm_bo_create", "GBM_BO_USE_LINEAR|GBM_BO_USE_RENDERING", "FAILED (no buffer)"));
            return rows;
        }

        var primeFd = -1;
        try
        {
            var stride = Gbm.BoGetStride(bo);
            var offset = Gbm.BoGetOffset(bo, 0);
            var modifier = Gbm.BoGetModifier(bo);
            primeFd = Gbm.BoGetFd(bo);
            rows.Add(new Row(
                "renderD128",
                "gbm_bo_create",
                $"{width}x{height} XRGB8888 pitch={stride} offset={offset} modifier=0x{modifier:X16}",
                primeFd >= 0 ? $"OK, dma-buf fd {primeFd}" : $"gbm_bo_get_fd FAILED ({Marshal.GetLastPInvokeError()})"));
            if (primeFd < 0)
            {
                return rows;
            }

            var card0Label = Path.GetFileName(card0Path);
            var card1Label = Path.GetFileName(card1Path);
            using var dumb = DumbBuffer.Create(card0Fd, width, height);
            rows.Add(new Row(card0Label, $"dumb handle {dumb.Handle}", "drmModeCreateDumbBuffer", $"OK, pitch={dumb.Pitch}, {dumb.Size} bytes"));

            // Control 1: the same AddFB2 call with a handle the card
            // definitely owns. Card0's file already holds dumb buffers from
            // the timing section, so the raw-fd row below is only meaningful
            // if the dma-buf fd number is above every GEM handle we own;
            // that is checked and reported rather than assumed.
            AddFb(card0Fd, width, height, dumb, rows, card0Label, $"dumb handle {dumb.Handle}", modifiers: false);

            // The dma-buf fd used raw as a handle, which is what the question
            // asks: does AddFB2 on card0 accept it?
            var highestHandle = Math.Max(dumb.Handle, 3u);
            if (primeFd > highestHandle)
            {
                AddFbFd(card0Fd, width, height, stride, offset, primeFd, rows, card0Label, $"dma-buf fd {primeFd} raw", modifiers: false);
                AddFbFd(card0Fd, width, height, stride, offset, primeFd, rows, card0Label, $"dma-buf fd {primeFd} raw", modifiers: true);
            }
            else
            {
                rows.Add(new Row(card0Label, $"dma-buf fd {primeFd}", "raw fd as handle", $"skipped: fd {primeFd} <= handle {highestHandle}, a collision is possible"));
            }

            // The explicit PRIME import path: this is the gate libdrm's
            // AddFB2 does not walk for you.
            var imported = LibDrm.PrimeFdToHandle(card0Fd, primeFd, out var card0Handle);
            rows.Add(new Row(card0Label, $"dma-buf fd {primeFd}", "drmPrimeFDToHandle", DescribePrime(imported)));
            uint importedFb = 0;
            if (imported == 0)
            {
                uint[] handles = [card0Handle, 0, 0, 0];
                uint[] pitches = [stride, 0, 0, 0];
                uint[] offsets = [offset, 0, 0, 0];
                fixed (uint* handlePointer = handles)
                fixed (uint* pitchPointer = pitches)
                fixed (uint* offsetPointer = offsets)
                {
                    var add = LibDrm.ModeAddFb2(card0Fd, width, height, LibDrm.FormatXrgb8888, handlePointer, pitchPointer, offsetPointer, out importedFb, 0);
                    rows.Add(new Row(card0Label, $"imported handle {card0Handle}", "drmModeAddFB2", DumbBuffer.Describe(add)));
                    if (add == 0)
                    {
                        LibDrm.ModeRmFb(card0Fd, importedFb);
                    }
                }

                LibDrm.CloseBufferHandle(card0Fd, card0Handle);
            }

            // Positive control on i915: the same dma-buf, imported by the
            // driver that exported it. If this works and card0 fails, the
            // dma-buf is sound and simpledrm is the obstacle.
            var i915Imported = LibDrm.PrimeFdToHandle(card1Fd, primeFd, out var i915Handle);
            rows.Add(new Row(card1Label, $"dma-buf fd {primeFd}", "drmPrimeFDToHandle", DescribePrime(i915Imported)));
            if (i915Imported == 0)
            {
                uint[] handles = [i915Handle, 0, 0, 0];
                uint[] pitches = [stride, 0, 0, 0];
                uint[] offsets = [offset, 0, 0, 0];
                fixed (uint* handlePointer = handles)
                fixed (uint* pitchPointer = pitches)
                fixed (uint* offsetPointer = offsets)
                {
                    var add = LibDrm.ModeAddFb2(card1Fd, width, height, LibDrm.FormatXrgb8888, handlePointer, pitchPointer, offsetPointer, out var fb, 0);
                    rows.Add(new Row(card1Label, $"imported handle {i915Handle}", "drmModeAddFB2", DumbBuffer.Describe(add)));
                    if (add == 0)
                    {
                        LibDrm.ModeRmFb(card1Fd, fb);
                    }
                }

                LibDrm.CloseBufferHandle(card1Fd, i915Handle);
            }
        }
        finally
        {
            if (primeFd >= 0)
            {
                Libc.Close(primeFd);
            }

            Gbm.BoDestroy(bo);
        }

        return rows;
    }

    /// <summary>drmPrimeFDToHandle returns -1 with errno set, unlike the drmMode* wrappers.</summary>
    private static string DescribePrime(int result)
    {
        if (result == 0)
        {
            return "OK";
        }

        var errno = Marshal.GetLastPInvokeError();
        return $"{Libc.ErrorName(errno)} ({errno})";
    }

    private static void AddFb(int fd, uint width, uint height, DumbBuffer dumb, List<Row> rows, string target, string source, bool modifiers)
    {
        uint[] handles = [dumb.Handle, 0, 0, 0];
        uint[] pitches = [dumb.Pitch, 0, 0, 0];
        uint[] offsets = [0, 0, 0, 0];
        fixed (uint* handlePointer = handles)
        fixed (uint* pitchPointer = pitches)
        fixed (uint* offsetPointer = offsets)
        {
            if (modifiers)
            {
                ulong[] modifier = [0, 0, 0, 0];
                fixed (ulong* modifierPointer = modifier)
                {
                    var add = LibDrm.ModeAddFb2WithModifiers(fd, width, height, LibDrm.FormatXrgb8888, handlePointer, pitchPointer, offsetPointer, modifierPointer, out var fb, LibDrm.ModeFbModifiers);
                    rows.Add(new Row(target, source, "drmModeAddFB2WithModifiers", DumbBuffer.Describe(add)));
                    if (add == 0)
                    {
                        LibDrm.ModeRmFb(fd, fb);
                    }
                }
            }
            else
            {
                var add = LibDrm.ModeAddFb2(fd, width, height, LibDrm.FormatXrgb8888, handlePointer, pitchPointer, offsetPointer, out var fb, 0);
                rows.Add(new Row(target, source, "drmModeAddFB2", DumbBuffer.Describe(add)));
                if (add == 0)
                {
                    LibDrm.ModeRmFb(fd, fb);
                }
            }
        }
    }

    /// <summary>AddFB2 with a dma-buf fd passed straight through as the handle.</summary>
    private static void AddFbFd(int fd, uint width, uint height, uint pitch, uint offset, int primeFd, List<Row> rows, string target, string source, bool modifiers)
    {
        uint[] handles = [unchecked((uint)primeFd), 0, 0, 0];
        uint[] pitches = [pitch, 0, 0, 0];
        uint[] offsets = [offset, 0, 0, 0];
        fixed (uint* handlePointer = handles)
        fixed (uint* pitchPointer = pitches)
        fixed (uint* offsetPointer = offsets)
        {
            var call = modifiers ? "drmModeAddFB2WithModifiers" : "drmModeAddFB2";
            if (modifiers)
            {
                ulong[] modifier = [0, 0, 0, 0];
                fixed (ulong* modifierPointer = modifier)
                {
                    var add = LibDrm.ModeAddFb2WithModifiers(fd, width, height, LibDrm.FormatXrgb8888, handlePointer, pitchPointer, offsetPointer, modifierPointer, out var fb, LibDrm.ModeFbModifiers);
                    rows.Add(new Row(target, source, call, DumbBuffer.Describe(add)));
                    if (add == 0)
                    {
                        LibDrm.ModeRmFb(fd, fb);
                    }
                }
            }
            else
            {
                var add = LibDrm.ModeAddFb2(fd, width, height, LibDrm.FormatXrgb8888, handlePointer, pitchPointer, offsetPointer, out var fb, 0);
                rows.Add(new Row(target, source, call, DumbBuffer.Describe(add)));
                if (add == 0)
                {
                    LibDrm.ModeRmFb(fd, fb);
                }
            }
        }
    }
}