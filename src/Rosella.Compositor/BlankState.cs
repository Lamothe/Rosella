namespace Rosella.Compositor;

/// <summary>
/// The blank latch: whether the black frame is up. The policy's word is
/// final — the panel shows nothing while blanked even if DPMS refuses to
/// cooperate — and the flip happens exactly once per blank. Render-loop
/// owned; the hardware state lives elsewhere.
/// </summary>
internal sealed class BlankState
{
    private bool _blackUp;

    /// <summary>Whether the black frame is currently up.</summary>
    internal bool IsBlackUp => _blackUp;

    /// <summary>
    /// Decides whether this turn needs a black flip. Blank needs one flip
    /// (and only one); wake resets the latch so the next blank flips again.
    /// Keyed on the policy alone: a hardware failure cannot stop the screen
    /// from going dark.
    /// </summary>
    internal bool NeedsBlackFlip(bool wantBlank)
    {
        if (!wantBlank)
        {
            _blackUp = false;
            return false;
        }

        if (_blackUp)
        {
            return false;
        }

        _blackUp = true;
        return true;
    }
}
