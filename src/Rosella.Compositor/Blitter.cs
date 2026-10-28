namespace Rosella.Compositor;

/// <summary>
/// Pixel copying into the scanout, all clipped. xrgb8888 sources copy
/// opaque; argb8888 sources blend source-over with premultiplied alpha (as
/// wl_shm defines it) — so client-side shadows and rounded corners stay
/// translucent instead of black squares. Scaling is nearest-neighbour:
/// source pixels map onto an arbitrary destination rectangle, which covers
/// HiDPI clients (1:1 at matching buffer scale), low-scale clients on a
/// high-scale output, and windows stretched to fill the work area.
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
        var startColumn = Math.Max(0, x);
        var columns = Math.Min(sourceWidth - (startColumn - x), targetWidth - startColumn);
        if (columns <= 0)
        {
            return;
        }

        for (var row = 0; row < sourceHeight; row++)
        {
            var targetY = y + row;
            if (targetY < 0 || targetY >= targetHeight)
            {
                continue;
            }

            var sourceOffset = ((row * sourceWidth) + (startColumn - x)) * 4;
            var targetOffset = (targetY * targetStride) + (startColumn * 4);
            source.Slice(sourceOffset, columns * 4).CopyTo(target.Slice(targetOffset, columns * 4));
        }
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
        if (sourceWidth <= 0 || sourceHeight <= 0 || destinationWidth <= 0 || destinationHeight <= 0)
        {
            return;
        }

        var opaque = format == ShmFormats.Xrgb8888;
        if (opaque && destinationWidth == sourceWidth && destinationHeight == sourceHeight && sourceStride == sourceWidth * 4)
        {
            Copy(target, targetWidth, targetHeight, targetStride, source, sourceWidth, sourceHeight, x, y);
            return;
        }

        var startX = Math.Max(0, x);
        var startY = Math.Max(0, y);
        var endX = Math.Min(targetWidth, x + destinationWidth);
        var endY = Math.Min(targetHeight, y + destinationHeight);
        if (startX >= endX || startY >= endY)
        {
            return;
        }

        var targetPixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target);
        var sourcePixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source);
        var sourceStrideWords = sourceStride / 4;
        var targetStrideWords = targetStride / 4;
        var targetWords = target.Length / 4;

        // One source column per target column, computed once and reused for
        // every row: the scaling divide is O(width) per blit, not per pixel.
        // Rented, not allocated (this runs per window per frame); the used
        // span is [0, columns) — rented arrays may be longer, and their tail
        // is stale data that must never be indexed.
        var columns = endX - startX;
        var columnMap = System.Buffers.ArrayPool<int>.Shared.Rent(columns);
        try
        {
            for (var targetX = startX; targetX < endX; targetX++)
            {
                var sourceColumn = (int)(((long)(targetX - x) * sourceWidth) / destinationWidth);
                columnMap[targetX - startX] = sourceColumn >= 0 && sourceColumn < sourceWidth ? sourceColumn : -1;
            }

            for (var targetY = startY; targetY < endY; targetY++)
            {
                // Nearest-neighbour source row for this target row.
                var sourceRow = (int)(((long)(targetY - y) * sourceHeight) / destinationHeight);
                if (sourceRow < 0 || sourceRow >= sourceHeight)
                {
                    continue;
                }

                var sourceRowOffset = sourceRow * sourceStrideWords;
                var targetRowOffset = targetY * targetStrideWords;
                for (var i = 0; i < columns; i++)
                {
                    var sourceColumn = columnMap[i];
                    if (sourceColumn < 0)
                    {
                        continue;
                    }

                    var targetX = startX + i;
                    var sourceIndex = sourceRowOffset + sourceColumn;
                    if ((uint)sourceIndex >= (uint)sourcePixels.Length)
                    {
                        continue;
                    }

                    var sourceColour = sourcePixels[sourceIndex];
                    var targetIndex = targetRowOffset + targetX;
                    if ((uint)targetIndex >= (uint)targetWords)
                    {
                        continue;
                    }

                    if (opaque)
                    {
                        // xrgb: the alpha byte is undefined; the scanout ignores
                        // it — copy verbatim, exactly like the 1:1 fast path.
                        targetPixels[targetIndex] = sourceColour;
                        continue;
                    }

                    var alpha = sourceColour >> 24;
                    if (alpha == 0)
                    {
                        continue;
                    }

                    if (alpha == 255)
                    {
                        targetPixels[targetIndex] = sourceColour;
                        continue;
                    }

                    // Source-over with premultiplied source: d = s + d * (1 - a).
                    var inverse = 255 - alpha;
                    var destination = targetPixels[targetIndex];
                    var red = ((sourceColour >> 16) & 0xFF) + ((((destination >> 16) & 0xFF) * inverse) / 255);
                    var green = ((sourceColour >> 8) & 0xFF) + ((((destination >> 8) & 0xFF) * inverse) / 255);
                    var blue = (sourceColour & 0xFF) + (((destination & 0xFF) * inverse) / 255);
                    targetPixels[targetIndex] = 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
                }
            }
        }
        finally
        {
            System.Buffers.ArrayPool<int>.Shared.Return(columnMap);
        }
    }
}
