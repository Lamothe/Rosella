using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// Pixel copying into the scanout, all clipped. xrgb8888 sources copy
/// opaque; argb8888 sources blend source-over with premultiplied alpha (as
/// wl_shm defines it) — so client-side shadows and rounded corners stay
/// translucent instead of black squares. Scaling is nearest-neighbour:
/// source pixels map onto an arbitrary destination rectangle, which covers
/// HiDPI clients (1:1 at matching buffer scale), low-scale clients on a
/// high-scale output, and windows stretched to fill the work area.
/// <para>
/// The generic nearest-neighbour sampler is the slow path, so the shapes
/// that actually occur get their own: a matching source and destination size
/// (no sampling at all — the usual case for a buffer-scale-2 client on a
/// scale-2 output), an integer scale factor (2× is the usual case for a
/// scale-1 client on a scale-2 output), and any row that turns out to be
/// fully opaque, which is a plain copy even for argb. Every path blends
/// through <see cref="Blend"/>, so results are identical to the sampler's.
/// </para>
/// </summary>
internal static class Blitter
{
    /// <summary>Opaque 1:1 row copy — the fast path for xrgb content.</summary>
    internal static void Copy(
        Span<byte> target,
        int targetWidth,
        int targetHeight,
        int targetStride,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y)
    {
        Copy(target, targetWidth, targetHeight, targetStride, source, sourceWidth, sourceHeight, x, y, DamageRect.For(targetWidth, targetHeight));
    }

    /// <summary>Opaque 1:1 row copy, additionally clipped to a damage region.</summary>
    internal static void Copy(
        Span<byte> target,
        int targetWidth,
        int targetHeight,
        int targetStride,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int x,
        int y,
        in DamageRect clip)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return;
        }

        Clip(x, y, sourceWidth, sourceHeight, targetWidth, targetHeight, clip, out var startX, out var endX, out var startY, out var endY);
        if (startX >= endX || startY >= endY)
        {
            return;
        }

        Identity(target, targetStride, source, sourceWidth * 4, x, y, startX, endX, startY, endY, opaque: true);
    }

    /// <summary>
    /// Blits the source into the destination rectangle at (x, y) with size
    /// (destinationWidth, destinationHeight), sampling nearest-neighbour and
    /// blending source-over for ARGB. <paramref name="sourceStride"/> is in
    /// bytes so callers may pass a sub-rectangle of a larger layer.
    /// </summary>
    internal static void Blit(
        Span<byte> target,
        int targetWidth,
        int targetHeight,
        int targetStride,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        uint format,
        int x,
        int y,
        int destinationWidth,
        int destinationHeight)
    {
        Blit(
            target,
            targetWidth,
            targetHeight,
            targetStride,
            source,
            sourceWidth,
            sourceHeight,
            sourceStride,
            format,
            x,
            y,
            destinationWidth,
            destinationHeight,
            DamageRect.For(targetWidth, targetHeight));
    }

    /// <summary>
    /// Blits, clipped to a damage region. Clipping only ever removes pixels
    /// the unclipped blit would have written, and never changes the mapping,
    /// so the kept pixels are identical.
    /// </summary>
    internal static void Blit(
        Span<byte> target,
        int targetWidth,
        int targetHeight,
        int targetStride,
        ReadOnlySpan<byte> source,
        int sourceWidth,
        int sourceHeight,
        int sourceStride,
        uint format,
        int x,
        int y,
        int destinationWidth,
        int destinationHeight,
        in DamageRect clip)
    {
        if (sourceWidth <= 0 || sourceHeight <= 0 || sourceStride <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            return;
        }

        Clip(x, y, destinationWidth, destinationHeight, targetWidth, targetHeight, clip, out var startX, out var endX, out var startY, out var endY);
        if (startX >= endX || startY >= endY)
        {
            return;
        }

        var opaque = format == ShmFormats.Xrgb8888;

        if (destinationWidth == sourceWidth && destinationHeight == sourceHeight)
        {
            // No sampling: the source maps onto the destination one pixel at
            // a time, which is what a buffer-scale-2 client on a scale-2
            // output asks for. Row-at-a-time, so a padded source stride (a
            // sub-rectangle) costs nothing either.
            Identity(target, targetStride, source, sourceStride, x, y, startX, endX, startY, endY, opaque);
            return;
        }

        if (destinationWidth % sourceWidth == 0 && destinationHeight % sourceHeight == 0
            && destinationWidth / sourceWidth == destinationHeight / sourceHeight
            && destinationWidth / sourceWidth >= 2)
        {
            // Integer magnification: each source pixel is a factor × factor
            // block, so a row expands once and repeats.
            Expand(
                target,
                targetStride,
                source,
                sourceStride,
                sourceWidth,
                sourceHeight,
                destinationWidth / sourceWidth,
                x,
                y,
                startX,
                endX,
                startY,
                endY,
                opaque);
            return;
        }

        Sampled(target, targetStride, source, sourceStride, sourceWidth, sourceHeight, destinationWidth, destinationHeight, x, y, startX, endX, startY, endY, opaque);
    }

    /// <summary>Destination rectangle, clipped to the target and the damage region.</summary>
    private static void Clip(
        int x,
        int y,
        int width,
        int height,
        int targetWidth,
        int targetHeight,
        in DamageRect clip,
        out int startX,
        out int endX,
        out int startY,
        out int endY)
    {
        startX = Math.Max(Math.Max(0, x), clip.X);
        startY = Math.Max(Math.Max(0, y), clip.Y);
        endX = Math.Min(Math.Min(targetWidth, x + width), clip.X + clip.W);
        endY = Math.Min(Math.Min(targetHeight, y + height), clip.Y + clip.H);
    }

    /// <summary>
    /// Identity mapping: one source pixel per destination pixel. Opaque rows
    /// (xrgb content, or an argb row whose alpha is all 255) are copied;
    /// anything else blends pixel by pixel.
    /// </summary>
    private static void Identity(
        Span<byte> target,
        int targetStride,
        ReadOnlySpan<byte> source,
        int sourceStride,
        int x,
        int y,
        int startX,
        int endX,
        int startY,
        int endY,
        bool opaque)
    {
        var firstColumn = startX - x;
        var bytes = (endX - startX) * 4;

        // Full-width rows on both sides: the region is one contiguous run in
        // each buffer, so an opaque copy is a single block move rather than a
        // memcpy per row (the old code took this path by accident of shape;
        // losing it would make a full-screen copy measurably slower).
        if (firstColumn == 0 && bytes == targetStride && bytes == sourceStride && opaque)
        {
            var blockOffset = (startY - y) * sourceStride;
            var blockLength = (endY - startY) * bytes;
            var blockTarget = startY * targetStride;
            if (blockOffset >= 0
                && blockLength > 0
                && blockOffset + blockLength <= source.Length
                && blockTarget + blockLength <= target.Length)
            {
                source.Slice(blockOffset, blockLength).CopyTo(target.Slice(blockTarget, blockLength));
                return;
            }
        }

        for (var targetY = startY; targetY < endY; targetY++)
        {
            var sourceOffset = ((targetY - y) * sourceStride) + (firstColumn * 4);

            // A source shorter than its stride and height claims is a caller
            // bug; clip it the way the sampler does rather than throwing.
            if (sourceOffset < 0 || sourceOffset + bytes > source.Length)
            {
                continue;
            }

            var targetOffset = (targetY * targetStride) + (startX * 4);
            var row = source.Slice(sourceOffset, bytes);
            if (opaque || RowIsOpaque(row))
            {
                row.CopyTo(target.Slice(targetOffset, bytes));
                continue;
            }

            BlendRow(target, targetOffset, row);
        }
    }

    /// <summary>
    /// Integer magnification: every source pixel becomes a
    /// <paramref name="factor"/> × <paramref name="factor"/> block. The
    /// source row is expanded once into a rented row, then copied (opaque)
    /// or blended (translucent) into each of its destination rows — so the
    /// work per destination pixel is a sequential read and a store, never a
    /// sampled gather.
    /// </summary>
    private static void Expand(
        Span<byte> target,
        int targetStride,
        ReadOnlySpan<byte> source,
        int sourceStride,
        int sourceWidth,
        int sourceHeight,
        int factor,
        int x,
        int y,
        int startX,
        int endX,
        int startY,
        int endY,
        bool opaque)
    {
        var targetPixels = MemoryMarshal.Cast<byte, uint>(target);
        var sourcePixels = MemoryMarshal.Cast<byte, uint>(source);
        var sourceStrideWords = sourceStride / 4;
        var targetStrideWords = targetStride / 4;
        var destinationWidth = sourceWidth * factor;

        var firstSourceRow = (startY - y) / factor;
        var lastSourceRow = ((endY - 1) - y) / factor;

        // Rented, not allocated: this runs per window per frame. The expanded
        // row is indexed by destination offset from x, so the clipped span is
        // the same slice for every destination row.
        var expanded = ArrayPool<uint>.Shared.Rent(destinationWidth);
        try
        {
            var row = expanded.AsSpan(0, destinationWidth);
            for (var sourceRow = firstSourceRow; sourceRow <= lastSourceRow; sourceRow++)
            {
                if ((uint)sourceRow >= (uint)sourceHeight)
                {
                    continue;
                }

                var sourceRowOffset = sourceRow * sourceStrideWords;
                if (sourceRowOffset + sourceWidth > sourcePixels.Length)
                {
                    continue;
                }

                // Nothing of this source row lands inside the clip: skip it
                // before expanding (damage is often a small part of a window).
                var firstTargetY = y + (sourceRow * factor);
                if (firstTargetY + factor <= startY || firstTargetY >= endY)
                {
                    continue;
                }

                var rowOpaque = opaque || RowIsOpaque(source.Slice(sourceRowOffset * 4, sourceWidth * 4));
                var at = 0;
                for (var column = 0; column < sourceWidth; column++)
                {
                    var colour = sourcePixels[sourceRowOffset + column];
                    for (var repeat = 0; repeat < factor; repeat++)
                    {
                        row[at++] = colour;
                    }
                }

                var span = row[(startX - x)..(endX - x)];
                for (var repeat = 0; repeat < factor; repeat++)
                {
                    var targetY = firstTargetY + repeat;
                    if (targetY < startY || targetY >= endY)
                    {
                        continue;
                    }

                    var targetRowOffset = targetY * targetStrideWords;
                    if (targetRowOffset + endX > targetPixels.Length)
                    {
                        continue;
                    }

                    var destination = targetPixels.Slice(targetRowOffset + startX, span.Length);
                    if (rowOpaque)
                    {
                        span.CopyTo(destination);
                        continue;
                    }

                    BlendPixels(destination, span);
                }
            }
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(expanded);
        }
    }

    /// <summary>
    /// The general nearest-neighbour sampler: arbitrary source and
    /// destination sizes. The one divide per column is hoisted into a rented
    /// map, and the per-pixel bounds checks become one check per row, so the
    /// inner loop is a load, a blend and a store.
    /// </summary>
    private static void Sampled(
        Span<byte> target,
        int targetStride,
        ReadOnlySpan<byte> source,
        int sourceStride,
        int sourceWidth,
        int sourceHeight,
        int destinationWidth,
        int destinationHeight,
        int x,
        int y,
        int startX,
        int endX,
        int startY,
        int endY,
        bool opaque)
    {
        var targetPixels = MemoryMarshal.Cast<byte, uint>(target);
        var sourcePixels = MemoryMarshal.Cast<byte, uint>(source);
        var sourceStrideWords = sourceStride / 4;
        var targetStrideWords = targetStride / 4;

        // One source column per target column, computed once and reused for
        // every row: the scaling divide is O(width) per blit, not per pixel.
        // Rented, not allocated (this runs per window per frame); the used
        // span is [0, columns) — rented arrays may be longer, and their tail
        // is stale data that must never be indexed.
        var columns = endX - startX;
        var columnMap = ArrayPool<int>.Shared.Rent(columns);
        try
        {
            for (var targetX = startX; targetX < endX; targetX++)
            {
                var sourceColumn = (int)(((long)(targetX - x) * sourceWidth) / destinationWidth);
                columnMap[targetX - startX] = (uint)sourceColumn < (uint)sourceWidth ? sourceColumn : -1;
            }

            for (var targetY = startY; targetY < endY; targetY++)
            {
                // Nearest-neighbour source row for this target row.
                var sourceRow = (int)(((long)(targetY - y) * sourceHeight) / destinationHeight);
                if ((uint)sourceRow >= (uint)sourceHeight)
                {
                    continue;
                }

                var sourceRowOffset = sourceRow * sourceStrideWords;
                if (sourceRowOffset + sourceWidth > sourcePixels.Length)
                {
                    continue;
                }

                var targetRowOffset = targetY * targetStrideWords;
                if (targetRowOffset >= targetPixels.Length)
                {
                    continue;
                }

                // Hoisted out of the inner loop: the row is known to fit, so
                // only the end of the destination buffer can still cut it.
                var rowEnd = Math.Min(endX, targetPixels.Length - targetRowOffset);
                for (var targetX = startX; targetX < rowEnd; targetX++)
                {
                    var sourceColumn = columnMap[targetX - startX];
                    if (sourceColumn < 0)
                    {
                        continue;
                    }

                    var colour = sourcePixels[sourceRowOffset + sourceColumn];
                    if (opaque)
                    {
                        // xrgb: the alpha byte is undefined; the scanout ignores
                        // it — copy verbatim, exactly like the 1:1 fast path.
                        targetPixels[targetRowOffset + targetX] = colour;
                        continue;
                    }

                    Blend(ref targetPixels[targetRowOffset + targetX], colour);
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(columnMap);
        }
    }

    /// <summary>
    /// One source column per clipped destination column, computed once per
    /// blit: the scaling divide is O(width), not O(width × rows). Rented, not
    /// allocated (this runs per window per frame), so it must be returned.
    /// </summary>
    private static int[] ColumnMap(int startX, int endX, int x, int sourceWidth, int destinationWidth)
    {
        var columns = endX - startX;
        var map = ArrayPool<int>.Shared.Rent(columns);
        for (var index = 0; index < columns; index++)
        {
            map[index] = (int)(((long)(startX + index - x) * sourceWidth) / destinationWidth);
        }

        return map;
    }

    /// <summary>
    /// Source-over with premultiplied source: d = s + d × (1 − a). Takes the
    /// destination by reference so an opaque or transparent pixel never reads
    /// it at all — the common case for a window body is a store with no load.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Blend(ref uint destination, uint source)
    {
        var alpha = source >> 24;
        if (alpha == 0)
        {
            return;
        }

        if (alpha == 255)
        {
            destination = source;
            return;
        }

        var inverse = 255 - alpha;
        var previous = destination;
        var red = ((source >> 16) & 0xFF) + ((((previous >> 16) & 0xFF) * inverse) / 255);
        var green = ((source >> 8) & 0xFF) + ((((previous >> 8) & 0xFF) * inverse) / 255);
        var blue = (source & 0xFF) + (((previous & 0xFF) * inverse) / 255);
        destination = 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
    }

    private static void BlendRow(Span<byte> target, int targetOffset, ReadOnlySpan<byte> row)
    {
        BlendPixels(
            MemoryMarshal.Cast<byte, uint>(target.Slice(targetOffset, row.Length)),
            MemoryMarshal.Cast<byte, uint>(row));
    }

    /// <summary>Pixel-wise source-over: the destination row against a source row.</summary>
    private static void BlendPixels(Span<uint> destination, ReadOnlySpan<uint> pixels)
    {
        for (var index = 0; index < destination.Length; index++)
        {
            Blend(ref destination[index], pixels[index]);
        }
    }

    /// <summary>
    /// Whether every pixel of the row is fully opaque (a plain copy then).
    /// The span search is vectorised: the scan is one pass over the row, and
    /// on a maximised ARGB window it costs a fifth of what the old
    /// per-pixel blend did.
    /// </summary>
    private static bool RowIsOpaque(ReadOnlySpan<byte> row) =>
        MemoryMarshal.Cast<byte, uint>(row).IndexOfAnyInRange(0u, 0xFEFFFFFFu) < 0;
}