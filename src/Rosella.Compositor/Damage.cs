namespace Rosella.Compositor;

/// <summary>
/// A rectangle in physical scanout pixels. Damage is spoken in output
/// pixels, not logical ones: it is what the blitter and the dumb buffers
/// index, so no scaling can be lost between the mark and the repaint.
/// </summary>
internal readonly record struct DamageRect(int X, int Y, int W, int H)
{
    internal static DamageRect Empty => default;

    /// <summary>The whole of an output of this size.</summary>
    internal static DamageRect For(int width, int height) => new(0, 0, width, height);

    internal bool IsEmpty => W <= 0 || H <= 0;

    internal long Area => IsEmpty ? 0 : (long)W * H;

    internal DamageRect Union(in DamageRect other)
    {
        if (IsEmpty)
        {
            return other;
        }

        if (other.IsEmpty)
        {
            return this;
        }

        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        var right = Math.Max(X + W, other.X + other.W);
        var bottom = Math.Max(Y + H, other.Y + other.H);
        return new DamageRect(left, top, right - left, bottom - top);
    }

    internal DamageRect Intersect(in DamageRect other)
    {
        var left = Math.Max(X, other.X);
        var top = Math.Max(Y, other.Y);
        var right = Math.Min(X + W, other.X + other.W);
        var bottom = Math.Min(Y + H, other.Y + other.H);
        return right <= left || bottom <= top ? Empty : new DamageRect(left, top, right - left, bottom - top);
    }

    internal bool Intersects(in DamageRect other) => !Intersect(other).IsEmpty;

    /// <summary>Whether the other rect lies entirely inside this one.</summary>
    internal bool Contains(in DamageRect other) =>
        !other.IsEmpty
        && other.X >= X && other.Y >= Y && other.X + other.W <= X + W && other.Y + other.H <= Y + H;
}

/// <summary>
/// One frame's damage: a bounded list of disjoint rectangles, in physical
/// output pixels.
/// <para>
/// A list rather than one bounding rectangle because the two things that
/// change on their own sit in opposite corners: the pointer anywhere on the
/// desktop, and the clock in the bottom-right. Their bounding box is very
/// nearly the whole screen, so collapsing to it would throw the win away.
/// Rects are unioned when they overlap (the list is always disjoint) and
/// merged into their bounding box once they either fragment past
/// <see cref="Capacity"/> or cover more than <see cref="CollapseRatio"/> of
/// it, because past that point one pass over the box beats N passes over
/// pieces.
/// </para>
/// <para>
/// Not thread-safe: the scene gate guards it, exactly like the dirty flag.
/// </para>
/// </summary>
internal sealed class DamageTracker
{
    /// <summary>Rects carried before the frame degrades to one bounding rect.</summary>
    internal const int Capacity = 8;

    /// <summary>Sum-of-areas to bounding-area ratio that makes one pass cheaper.</summary>
    private const double CollapseRatio = 0.5;

    private readonly DamageRect[] _rects = new DamageRect[Capacity];
    private int _count;
    private bool _full;

    internal bool IsFull => _full;

    internal int Count => _full ? 1 : _count;

    /// <summary>Everything changed — or something changed and we cannot say what.</summary>
    internal void MarkFull()
    {
        _full = true;
        _count = 0;
    }

    /// <summary>Adds a changed rectangle (unclipped; <see cref="Take"/> clips).</summary>
    internal void Add(in DamageRect rect)
    {
        if (_full || rect.IsEmpty)
        {
            return;
        }

        var merged = rect;
        var index = 0;
        while (index < _count)
        {
            if (_rects[index].Intersects(merged))
            {
                merged = _rects[index].Union(merged);
                _rects[index] = _rects[--_count];
                index = 0;
                continue;
            }

            index++;
        }

        if (_count >= Capacity)
        {
            // Past what a list is worth: fall back to one bounding rect
            // rather than growing (this runs on the render and input paths).
            _rects[0] = Bounding().Union(merged);
            _count = 1;
            return;
        }

        _rects[_count++] = merged;
    }

    /// <summary>
    /// Copies this frame's damage, clipped to the output, into
    /// <paramref name="destination"/> and clears the tracker. A full repaint
    /// arrives as one whole-output rect so the caller has a single path;
    /// zero rects means nothing changed at all.
    /// </summary>
    internal int Take(int width, int height, Span<DamageRect> destination)
    {
        var bounds = DamageRect.For(width, height);
        var clipped = 0;
        if (_full)
        {
            _full = false;
            _count = 0;
            destination[0] = bounds;
            return 1;
        }

        Span<DamageRect> pieces = stackalloc DamageRect[Capacity];
        for (var index = 0; index < _count; index++)
        {
            var piece = _rects[index].Intersect(bounds);
            if (!piece.IsEmpty)
            {
                pieces[clipped++] = piece;
            }
        }

        _count = 0;
        if (clipped == 0)
        {
            return 0;
        }

        // Scattered pieces cost a layer pass each; when they nearly tile
        // their bounding box, one pass over the box is the cheaper frame.
        if (clipped > 1)
        {
            var box = pieces[0];
            long total = 0;
            for (var index = 0; index < clipped; index++)
            {
                box = box.Union(pieces[index]);
                total += pieces[index].Area;
            }

            if (total >= (long)(box.Area * CollapseRatio))
            {
                destination[0] = box;
                return 1;
            }
        }

        pieces[..clipped].CopyTo(destination);
        return clipped;
    }

    private DamageRect Bounding()
    {
        var bounds = DamageRect.Empty;
        for (var index = 0; index < _count; index++)
        {
            bounds = bounds.Union(_rects[index]);
        }

        return bounds;
    }
}

/// <summary>
/// A small fixed-capacity set of disjoint rectangles, with a hard bound: the
/// render loop uses it to work out which parts of a damage region no opaque
/// window covers, and a pathological frame degrades to "paint everything"
/// instead of allocating or looping.
/// </summary>
internal sealed class RectList
{
    internal const int Capacity = 16;

    private readonly DamageRect[] _rects = new DamageRect[Capacity];
    private int _count;

    /// <summary>Set when the set outgrew its bound: the caller must treat it as "all of it".</summary>
    internal bool Overflowed { get; private set; }

    internal int Count => _count;

    internal DamageRect this[int index] => _rects[index];

    internal long Area
    {
        get
        {
            long area = 0;
            for (var index = 0; index < _count; index++)
            {
                area += _rects[index].Area;
            }

            return area;
        }
    }

    /// <summary>Starts again from a single rectangle.</summary>
    internal void Reset(in DamageRect rect)
    {
        Overflowed = false;
        _count = 0;
        if (!rect.IsEmpty)
        {
            _rects[_count++] = rect;
        }
    }

    /// <summary>Removes the hole's pixels, splitting any rect it cuts.</summary>
    internal void Subtract(in DamageRect hole)
    {
        if (Overflowed || hole.IsEmpty)
        {
            return;
        }

        for (var index = 0; index < _count; index++)
        {
            var rect = _rects[index];
            var overlap = rect.Intersect(hole);
            if (overlap.IsEmpty)
            {
                continue;
            }

            // Replace the cut rect with up to four pieces: above, below,
            // and the two side strips alongside the hole.
            if (_count + 3 > Capacity)
            {
                Overflowed = true;
                return;
            }

            _rects[index] = _rects[--_count];
            index--;
            AddPiece(new DamageRect(rect.X, rect.Y, rect.W, overlap.Y - rect.Y));
            AddPiece(new DamageRect(rect.X, overlap.Y + overlap.H, rect.W, (rect.Y + rect.H) - (overlap.Y + overlap.H)));
            AddPiece(new DamageRect(rect.X, overlap.Y, overlap.X - rect.X, overlap.H));
            AddPiece(new DamageRect(overlap.X + overlap.W, overlap.Y, (rect.X + rect.W) - (overlap.X + overlap.W), overlap.H));
        }
    }

    private void AddPiece(in DamageRect rect)
    {
        if (!rect.IsEmpty)
        {
            _rects[_count++] = rect;
        }
    }
}
/// <summary>
/// What the two scanout buffers each still owe a repaint (their "buffer
/// age"): the damage of the frames they were not the target of.
/// <para>
/// Two dumb buffers alternate, so the one being written last showed the
/// frame from two flips ago. Repainting only this frame's damage would bring
/// pixels that changed last frame back from the dead — the pointer trail
/// that every compositor has to avoid. A frame therefore repaints its own
/// damage plus the damage of the frame it skipped, which is exact and stays
/// small.
/// </para>
/// </summary>
internal sealed class BufferAge
{
    private readonly DamageTracker[] _pending = [new DamageTracker(), new DamageTracker()];

    /// <summary>Marks both buffers as owing a whole repaint (startup, a blank, a VT switch).</summary>
    internal void MarkFull()
    {
        _pending[0].MarkFull();
        _pending[1].MarkFull();
    }

    /// <summary>
    /// The regions to repaint when <paramref name="damage"/> is about to be
    /// drawn into <paramref name="buffer"/>: this frame's damage and whatever
    /// that buffer missed. The other buffer inherits this frame's damage.
    /// </summary>
    internal int Take(int buffer, ReadOnlySpan<DamageRect> damage, int width, int height, Span<DamageRect> repaint)
    {
        var pending = _pending[buffer];
        var other = _pending[1 - buffer];
        for (var index = 0; index < damage.Length; index++)
        {
            pending.Add(damage[index]);
            other.Add(damage[index]);
        }

        return pending.Take(width, height, repaint);
    }
}

/// <summary>
/// What one composite touched: the pixels the desktop filled and the pixels
/// the windows covered (translucent ones may paint fewer than they cover).
/// The damage path exists to shrink these numbers, so they are returned
/// rather than guessed at — a test can compare a small-damage frame against
/// a full one.
/// </summary>
internal readonly record struct RenderCost(long BackgroundPixels, long SurfacePixels, int Surfaces)
{
    /// <summary>Pixels the frame had to composite, background and windows together.</summary>
    internal long Pixels => BackgroundPixels + SurfacePixels;
}
