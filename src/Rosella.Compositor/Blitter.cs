namespace Rosella.Compositor;

/// <summary>
/// Copies XRGB8888 rows into the scanout with clipping. The alpha channel of
/// argb8888 sources is ignored (opaque copy) until blending lands.
/// </summary>
internal static class Blitter
{
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
}
