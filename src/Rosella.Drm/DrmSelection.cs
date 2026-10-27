namespace Rosella.Drm;

/// <summary>
/// Pure selection rules for modes and CRTCs — opinionated defaults, no
/// configuration surface. Kept side-effect free for testing.
/// </summary>
public static class DrmSelection
{
    /// <summary>
    /// Picks the mode to drive: the preferred mode if flagged, otherwise the
    /// largest mode, otherwise the first.
    /// </summary>
    public static DrmMode SelectMode(IReadOnlyList<DrmMode> modes)
    {
        if (modes.Count == 0)
        {
            throw new ArgumentException("Connector reports no modes.", nameof(modes));
        }

        var chosen = modes[0];
        foreach (var mode in modes)
        {
            if (mode.IsPreferred)
            {
                return mode;
            }

            if (mode.Area > chosen.Area)
            {
                chosen = mode;
            }
        }

        return chosen;
    }

    /// <summary>
    /// Picks the CRTC for a connector's encoder: the CRTC the encoder is
    /// already attached to when possible, otherwise the first compatible one
    /// from the encoder's possible_crtcs mask.
    /// </summary>
    public static uint SelectCrtc(IReadOnlyList<uint> crtcIds, DrmEncoder encoder)
    {
        if (crtcIds.Count == 0)
        {
            throw new ArgumentException("Card reports no CRTCs.", nameof(crtcIds));
        }

        if (encoder.CrtcId != 0 && crtcIds.Contains(encoder.CrtcId))
        {
            return encoder.CrtcId;
        }

        for (var i = 0; i < crtcIds.Count; i++)
        {
            if ((encoder.PossibleCrtcs & (1u << i)) != 0)
            {
                return crtcIds[i];
            }
        }

        throw new InvalidOperationException($"Encoder {encoder.Id} is compatible with none of the card's CRTCs.");
    }
}
