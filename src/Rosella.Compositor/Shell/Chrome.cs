using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>A rectangle in output pixels.</summary>
internal readonly record struct Rect(int X, int Y, int W, int H)
{
    internal bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
}

/// <summary>Rectangle primitives for the shell chrome, all clipped.</summary>
internal static class Chrome
{
    internal static void FillRect(Span<byte> pixels, int width, int height, int stride, Rect rect, uint colour)
    {
        var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        for (var y = Math.Max(0, rect.Y); y < Math.Min(height, rect.Y + rect.H); y++)
        {
            for (var x = Math.Max(0, rect.X); x < Math.Min(width, rect.X + rect.W); x++)
            {
                rowPixels[(y * strideWords) + x] = colour;
            }
        }
    }

    internal static void FrameRect(Span<byte> pixels, int width, int height, int stride, Rect rect, uint colour)
    {
        FillRect(pixels, width, height, stride, new Rect(rect.X, rect.Y, rect.W, 1), colour);
        FillRect(pixels, width, height, stride, new Rect(rect.X, rect.Y + rect.H - 1, rect.W, 1), colour);
        FillRect(pixels, width, height, stride, new Rect(rect.X, rect.Y, 1, rect.H), colour);
        FillRect(pixels, width, height, stride, new Rect(rect.X + rect.W - 1, rect.Y, 1, rect.H), colour);
    }
}
