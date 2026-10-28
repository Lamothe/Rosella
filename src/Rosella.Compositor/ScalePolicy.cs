namespace Rosella.Compositor;

/// <summary>
/// The output scale policy: how many physical pixels make one logical
/// pixel on a given mode. Follows mutter's calculate_scale — target 135
/// DPI for small screens, 110 for large ones — with two opinionated
/// simplifications: integer scales only (no fractional scaling), and a
/// resolution fallback for outputs that report no physical size (or the
/// 96-DPI placeholder simpledrm fabricates when the firmware reports
/// none).
/// </summary>
internal static class ScalePolicy
{
    /// <summary>Target DPI for panels under 20 inches (mutter's mobile target).</summary>
    private const double MobileTargetDpi = 135;

    /// <summary>Target DPI for panels of 20 inches and up (mutter's large target).</summary>
    private const double LargeTargetDpi = 110;

    private const double LargeScreenInches = 20;

    /// <summary>The integer scale for one mode and its physical size.</summary>
    internal static int For(int widthPx, int heightPx, uint mmWidth, uint mmHeight)
    {
        if (mmWidth == 0 || mmHeight == 0)
        {
            return ForUnknownSize(widthPx, heightPx);
        }

        var diagonalMm = Math.Sqrt((mmWidth * (double)mmWidth) + (mmHeight * (double)mmHeight));
        var diagonalInches = diagonalMm / 25.4;
        var diagonalPx = Math.Sqrt((widthPx * (double)widthPx) + (heightPx * (double)heightPx));
        var physicalDpi = diagonalPx / diagonalInches;

        // simpledrm (and some firmware) fabricates a size at exactly 96 DPI
        // when none is reported: the mm size is exactly px x 25.4/96 on both
        // axes. Only that exact placeholder is distrusted — a real panel that
        // genuinely measures 96 DPI keeps the formula (mutter's target-scale
        // math is the source of truth for real hardware).
        if (IsFabricatedSize(widthPx, heightPx, mmWidth, mmHeight))
        {
            return ForUnknownSize(widthPx, heightPx);
        }

        var targetDpi = diagonalInches < LargeScreenInches ? MobileTargetDpi : LargeTargetDpi;
        var perfectScale = physicalDpi / targetDpi;
        return Math.Clamp((int)Math.Round(perfectScale, MidpointRounding.AwayFromZero), 1, 4);
    }

    /// <summary>No usable physical size: laptop-class panels get 2x.</summary>
    private static int ForUnknownSize(int widthPx, int heightPx) =>
        widthPx >= 2560 && heightPx >= 1600 ? 2 : 1;

    /// <summary>
    /// The 96-DPI placeholder: both axes exactly px x 25.4/96 mm (to the
    /// millimetre simpledrm fabricates). Real panels report their measured
    /// size and land within a millimetre of that only by coincidence on both
    /// axes at once.
    /// </summary>
    private static bool IsFabricatedSize(int widthPx, int heightPx, uint mmWidth, uint mmHeight) =>
        Math.Abs(mmWidth - (widthPx * 25.4 / 96.0)) <= 1.0
        && Math.Abs(mmHeight - (heightPx * 25.4 / 96.0)) <= 1.0;
}
