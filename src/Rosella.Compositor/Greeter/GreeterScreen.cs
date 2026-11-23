namespace Rosella.Compositor.Greeter;

/// <summary>
/// The login screen itself: a centred card with the username, a masked
/// secret, the current prompt or error, and the session picker. It is a
/// Scene overlay because a greeter has no clients; the primitives are the
/// shell's own (Chrome and ShellText), so scale 2 is 2× text and 2× fields
/// with no greeter-specific scaling policy.
/// </summary>
internal sealed class GreeterScreen : ISceneOverlay
{
    private const uint CardColour = 0xFF1D222B;
    private const uint CardBorder = 0xFF39404D;
    private const uint CardShadow = 0xFF11151B;
    private const uint FieldColour = 0xFF262E3A;
    private const uint FieldBorder = 0xFF3C4554;
    private const uint FieldFocused = 0xFF7B86E8;
    private const uint TitleColour = 0xFFFFFFFF;
    private const uint TextColour = 0xFFE9ECF1;
    private const uint MutedColour = 0xFF9AA4B2;
    private const uint PlaceholderColour = 0xFF6E7887;
    private const uint ErrorColour = 0xFFFF7B7B;

    private const int TitleEm = 26;
    private const int LabelEm = 14;
    private const int ValueEm = 20;
    private const int StatusEm = 15;
    private const int SessionEm = 17;
    private const int HintEm = 13;

    /// <summary>Longest masked run drawn; a pasted 128-character secret must not escape the field.</summary>
    private const int MaxMask = 32;

    private const int SessionLabelWidth = 88;
    private const int FieldInset = 14;

    private readonly GreeterState _state;

    internal GreeterScreen(GreeterState state)
    {
        _state = state;
    }

    /// <summary>The echo-off rendering of a secret: one bullet per character, capped.</summary>
    internal static string Mask(string secret) =>
        new string('•', Math.Min(secret.Length, MaxMask));

    /// <summary>
    /// The secret field's text. A secret prompt is masked; a visible prompt
    /// (an OTP, a printed code) is echoed, because greetd said so.
    /// </summary>
    internal static string SecretText(GreeterSnapshot snapshot)
    {
        if (snapshot.Secret.Length > 0)
        {
            return snapshot.SecretPrompt ? Mask(snapshot.Secret) : snapshot.Secret;
        }

        return snapshot.AwaitingInput && !snapshot.SecretPrompt ? "Response" : "PIN or password";
    }

    public void Draw(Span<byte> pixels, int width, int height, int stride, int scale)
    {
        // The overlay owns every pixel: no desktop, no wallpaper, no cursor.
        Background.Render(pixels, width, height, stride);

        var snapshot = _state.Snapshot();
        var layout = GreeterLayout.Compute(width, height, scale);

        Chrome.FillRoundedRect(pixels, width, height, stride, Offset(layout.Card, 0, 6), 18, CardShadow, scale);
        Chrome.FillRoundedRect(pixels, width, height, stride, layout.Card, 16, CardBorder, scale);
        Chrome.FillRoundedRect(pixels, width, height, stride, Inset(layout.Card, 1), 15, CardColour, scale);

        DrawText(pixels, width, height, stride, layout.Title, "Rosella", TitleColour, TitleEm, scale, centred: true);

        var editable = !snapshot.Busy || snapshot.AwaitingInput;
        var usernameFocused = snapshot.Focus == GreeterField.Username;
        var secretFocused = snapshot.Focus == GreeterField.Secret;

        DrawText(pixels, width, height, stride, layout.UsernameLabel, "Username", MutedColour, LabelEm, scale);
        DrawField(
            pixels, width, height, stride, layout.UsernameField, usernameFocused,
            snapshot.Username.Length > 0 ? snapshot.Username : "Your username",
            snapshot.Username.Length > 0 ? TextColour : PlaceholderColour,
            editable && usernameFocused,
            snapshot.Username.Length == 0,
            scale);

        DrawText(pixels, width, height, stride, layout.SecretLabel, "PIN or password", MutedColour, LabelEm, scale);

        // A secret prompt is echo-off; a visible prompt (an OTP, a printed
        // code) is not, and greetd says which one this is.
        DrawField(
            pixels, width, height, stride, layout.SecretField, secretFocused,
            SecretText(snapshot),
            snapshot.Secret.Length > 0 ? TextColour : PlaceholderColour,
            editable && secretFocused,
            snapshot.Secret.Length == 0,
            scale);

        var (status, statusColour) = StatusFor(snapshot);
        if (status.Length > 0)
        {
            DrawText(
                pixels, width, height, stride, layout.StatusLine,
                Fit(status, StatusEm, layout.StatusLine.W),
                statusColour, StatusEm, scale);
        }

        DrawSessionPicker(pixels, width, height, stride, layout.SessionRow, snapshot, scale);

        var hint = snapshot.Busy
            ? snapshot.AwaitingInput ? "Enter answer · Esc cancel" : "Waiting for greetd…"
            : snapshot.Focus == GreeterField.Secret && snapshot.Username.Length > 0
                ? "Enter sign in · Esc clear · Tab change user"
                : "Enter sign in · Esc clear · Tab field";
        DrawText(pixels, width, height, stride, layout.HintLine, hint, MutedColour, HintEm, scale, centred: true);
    }

    private static (string Text, uint Colour) StatusFor(GreeterSnapshot snapshot)
    {
        if (snapshot.MessageIsError)
        {
            return (snapshot.Message, ErrorColour);
        }

        if (snapshot.Prompt.Length > 0)
        {
            return (snapshot.Prompt, TextColour);
        }

        if (snapshot.Message.Length > 0)
        {
            return (snapshot.Message, MutedColour);
        }

        return (string.Empty, MutedColour);
    }

    private static void DrawField(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        Rect field,
        bool focused,
        string text,
        uint textColour,
        bool caret,
        bool placeholder,
        int scale)
    {
        Chrome.FillRoundedRect(pixels, width, height, stride, field, 8, focused ? FieldFocused : FieldBorder, scale);
        Chrome.FillRoundedRect(pixels, width, height, stride, Inset(field, focused ? 2 : 1), 7, FieldColour, scale);

        var inset = new Rect(field.X + FieldInset, field.Y, Math.Max(0, field.W - (2 * FieldInset)), field.H);
        DrawText(pixels, width, height, stride, inset, text, textColour, ValueEm, scale);

        if (!caret)
        {
            return;
        }

        // The caret sits after real text; a placeholder is not text, so the
        // caret goes to the start of the field.
        var (textWidth, lineHeight) = ShellText.Measure(text, ValueEm);
        var caretX = inset.X + (placeholder ? 0 : textWidth + 2);
        if (caretX + 2 >= field.X + field.W)
        {
            return;
        }

        var top = field.Y + ((field.H - lineHeight) / 2) + 2;
        Chrome.FillRect(pixels, width, height, stride, new Rect(caretX, top, 2, Math.Max(2, lineHeight - 4)), FieldFocused, scale);
    }

    private static void DrawSessionPicker(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        Rect row,
        GreeterSnapshot snapshot,
        int scale)
    {
        Chrome.FillRoundedRect(pixels, width, height, stride, row, 8, FieldBorder, scale);
        Chrome.FillRoundedRect(pixels, width, height, stride, Inset(row, 1), 7, FieldColour, scale);

        var inset = new Rect(row.X + 12, row.Y, Math.Max(0, row.W - 24), row.H);
        DrawText(pixels, width, height, stride, inset, "Session", MutedColour, LabelEm, scale);

        var nameBox = new Rect(inset.X + SessionLabelWidth, inset.Y, Math.Max(0, inset.W - SessionLabelWidth - 64), inset.H);
        DrawText(
            pixels, width, height, stride, nameBox,
            Fit(snapshot.SessionName, SessionEm, nameBox.W),
            TextColour, SessionEm, scale);

        var hintBox = new Rect(inset.X + Math.Max(0, inset.W - 60), inset.Y, Math.Min(60, inset.W), inset.H);
        DrawText(pixels, width, height, stride, hintBox, "Up/Down", MutedColour, HintEm, scale, centred: true);
    }

    /// <summary>
    /// Draws one line vertically centred in a logical box. ShellText takes
    /// physical pixels and a logical em, so the box's origin is scaled here
    /// and nowhere else.
    /// </summary>
    private static void DrawText(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        Rect box,
        string text,
        uint colour,
        int em,
        int scale,
        bool centred = false)
    {
        if (text.Length == 0 || box.W <= 0 || box.H <= 0)
        {
            return;
        }

        var (textWidth, lineHeight) = ShellText.Measure(text, em);
        var x = centred ? box.X + Math.Max(0, (box.W - textWidth) / 2) : box.X;
        var y = box.Y + Math.Max(0, (box.H - lineHeight) / 2);
        ShellText.DrawText(pixels, width, height, stride, text, x * scale, y * scale, colour, em, scale);
    }

    /// <summary>Trims text with an ellipsis until it fits the given logical width.</summary>
    private static string Fit(string text, int em, int maxWidth)
    {
        if (maxWidth <= 0)
        {
            return string.Empty;
        }

        if (ShellText.Measure(text, em).Width <= maxWidth)
        {
            return text;
        }

        var trimmed = text;
        while (trimmed.Length > 0 && ShellText.Measure(trimmed + "…", em).Width > maxWidth)
        {
            trimmed = trimmed[..^1];
        }

        return trimmed.Length == 0 ? "…" : trimmed + "…";
    }

    private static Rect Inset(Rect rect, int by) =>
        new(rect.X + by, rect.Y + by, Math.Max(0, rect.W - (2 * by)), Math.Max(0, rect.H - (2 * by)));

    private static Rect Offset(Rect rect, int dx, int dy) =>
        new(rect.X + dx, rect.Y + dy, rect.W, rect.H);
}