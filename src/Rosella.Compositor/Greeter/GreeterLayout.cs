namespace Rosella.Compositor.Greeter;

/// <summary>
/// One login screen's rectangles, in logical pixels. The card is centred on
/// the output and its rows are fixed logical heights, so the same layout is
/// produced at scale 1 (mercury's 1× panel) and scale 2 (silver's 2880×1800
/// panel): only the physical size of every rectangle changes.
///
/// Drawing and (future) hit-testing use this one layout, exactly as the
/// taskbar's layout is shared between Bar.Draw and InputRouter.
/// </summary>
internal readonly record struct GreeterLayout(
    Rect Card,
    Rect Title,
    Rect UsernameLabel,
    Rect UsernameField,
    Rect SecretLabel,
    Rect SecretField,
    Rect StatusLine,
    Rect SessionRow,
    Rect HintLine)
{
    /// <summary>Card width in logical pixels: a comfortable single column at 1× and 2×.</summary>
    internal const int CardWidth = 440;

    /// <summary>Space between the card and the output edge, when the screen is small.</summary>
    internal const int Margin = 24;

    internal const int Padding = 28;
    internal const int TitleHeight = 34;
    internal const int LabelHeight = 18;
    internal const int FieldHeight = 42;
    internal const int StatusHeight = 22;
    internal const int SessionHeight = 34;
    internal const int HintHeight = 18;
    internal const int TitleGap = 14;
    internal const int LabelGap = 4;
    internal const int FieldGap = 16;
    internal const int StatusGap = 12;
    internal const int SessionGap = 10;
    internal const int HintGap = 8;

    /// <summary>The card's height before clamping to a short output.</summary>
    internal static int ContentHeight =>
        TitleHeight + TitleGap
        + LabelHeight + LabelGap + FieldHeight + FieldGap
        + LabelHeight + LabelGap + FieldHeight + StatusGap
        + StatusHeight + SessionGap
        + SessionHeight + HintGap
        + HintHeight
        + (Padding * 2);

    /// <summary>
    /// Lays out the centred card for a physical output and the compositor's
    /// scale. Physical pixels are divided by scale exactly once: everything
    /// inside is logical, which is what makes 2× text and 2× hit targets fall
    /// out without a second policy.
    /// </summary>
    internal static GreeterLayout Compute(int physicalWidth, int physicalHeight, int scale)
    {
        scale = Math.Max(1, scale);
        var width = Math.Max(1, physicalWidth / scale);
        var height = Math.Max(1, physicalHeight / scale);

        var cardWidth = Math.Clamp(CardWidth, 1, Math.Max(1, width - (2 * Margin)));

        // The height is never compressed: the rows are fixed logical boxes,
        // and shrinking only the border would leave the last rows drawn
        // outside the card. On an output too short for the card the card is
        // centred and clipped by the screen instead.
        var cardHeight = ContentHeight;
        var cardX = (width - cardWidth) / 2;
        var cardY = (height - cardHeight) / 2;

        var x = cardX + Padding;
        var innerWidth = Math.Max(1, cardWidth - (2 * Padding));
        var y = cardY + Padding;

        var title = new Rect(x, y, innerWidth, TitleHeight);
        y += TitleHeight + TitleGap;

        var usernameLabel = new Rect(x, y, innerWidth, LabelHeight);
        y += LabelHeight + LabelGap;
        var usernameField = new Rect(x, y, innerWidth, FieldHeight);
        y += FieldHeight + FieldGap;

        var secretLabel = new Rect(x, y, innerWidth, LabelHeight);
        y += LabelHeight + LabelGap;
        var secretField = new Rect(x, y, innerWidth, FieldHeight);
        y += FieldHeight + StatusGap;

        var status = new Rect(x, y, innerWidth, StatusHeight);
        y += StatusHeight + SessionGap;

        var session = new Rect(x, y, innerWidth, SessionHeight);
        y += SessionHeight + HintGap;

        var hint = new Rect(x, y, innerWidth, HintHeight);

        return new GreeterLayout(
            new Rect(cardX, cardY, cardWidth, cardHeight),
            title,
            usernameLabel,
            usernameField,
            secretLabel,
            secretField,
            status,
            session,
            hint);
    }

    /// <summary>A logical rectangle in output pixels: the only scaling the renderer does.</summary>
    internal static Rect Physical(Rect rect, int scale) => rect.Scaled(Math.Max(1, scale));
}