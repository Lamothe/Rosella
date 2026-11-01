using Rosella.Input;

namespace Rosella.Compositor.Greeter;

/// <summary>A greeter keyboard action, decoupled from evdev for testing.</summary>
internal enum GreeterKeyAction
{
    None,
    Character,
    Enter,
    Escape,
    Backspace,
    Tab,
    Up,
    Down,
}

/// <summary>One translated key: an action and, for Character, the text.</summary>
internal readonly record struct GreeterKey(GreeterKeyAction Action, char Character = '\0');

/// <summary>
/// evdev key code to login-form action. Rosella's keyboard layout is the one
/// US QWERTY map it generated at M6, so the greeter uses the same layout
/// rather than inventing a second one; Caps Lock is disabled by the xkb
/// option on this machine and is deliberately not modelled here.
/// </summary>
internal static class GreeterKeys
{
    // Codes the compositor does not already name in InputConstants.
    private const ushort KeyBackspace = 14;
    private const ushort KeySpace = 57;
    private const ushort KeyMinus = 12;
    private const ushort KeyEqual = 13;
    private const ushort KeyLeftBrace = 26;
    private const ushort KeyRightBrace = 27;
    private const ushort KeySemicolon = 39;
    private const ushort KeyApostrophe = 40;
    private const ushort KeyGrave = 41;
    private const ushort KeyBackslash = 43;
    private const ushort KeyComma = 51;
    private const ushort KeyDot = 52;
    private const ushort KeySlash = 53;
    private const ushort KeyPadDot = 83;

    /// <summary>Translate one key. <paramref name="shift"/> is the physical shift state.</summary>
    internal static GreeterKey Map(ushort code, bool shift) => code switch
    {
        InputConstants.KeyEnter => new GreeterKey(GreeterKeyAction.Enter),
        InputConstants.KeyEsc => new GreeterKey(GreeterKeyAction.Escape),
        InputConstants.KeyTab => new GreeterKey(GreeterKeyAction.Tab),
        KeyBackspace => new GreeterKey(GreeterKeyAction.Backspace),
        InputConstants.KeyUp => new GreeterKey(GreeterKeyAction.Up),
        InputConstants.KeyDown => new GreeterKey(GreeterKeyAction.Down),

        // Number row.
        2 => Character('1', '!', shift),
        3 => Character('2', '@', shift),
        4 => Character('3', '#', shift),
        5 => Character('4', '$', shift),
        6 => Character('5', '%', shift),
        7 => Character('6', '^', shift),
        8 => Character('7', '&', shift),
        9 => Character('8', '*', shift),
        10 => Character('9', '(', shift),
        11 => Character('0', ')', shift),

        // Top row: qwertyuiop.
        16 => Letter('q', shift),
        17 => Letter('w', shift),
        18 => Letter('e', shift),
        19 => Letter('r', shift),
        20 => Letter('t', shift),
        21 => Letter('y', shift),
        22 => Letter('u', shift),
        23 => Letter('i', shift),
        24 => Letter('o', shift),
        25 => Letter('p', shift),

        // Home row: asdfghjkl.
        30 => Letter('a', shift),
        31 => Letter('s', shift),
        32 => Letter('d', shift),
        33 => Letter('f', shift),
        34 => Letter('g', shift),
        35 => Letter('h', shift),
        36 => Letter('j', shift),
        37 => Letter('k', shift),
        38 => Letter('l', shift),

        // Bottom row: zxcvbnm.
        44 => Letter('z', shift),
        45 => Letter('x', shift),
        46 => Letter('c', shift),
        47 => Letter('v', shift),
        48 => Letter('b', shift),
        49 => Letter('n', shift),
        50 => Letter('m', shift),

        // Punctuation.
        KeyMinus => Character('-', '_', shift),
        KeyEqual => Character('=', '+', shift),
        KeyLeftBrace => Character('[', '{', shift),
        KeyRightBrace => Character(']', '}', shift),
        KeySemicolon => Character(';', ':', shift),
        KeyApostrophe => Character('\'', '"', shift),
        KeyGrave => Character('`', '~', shift),
        KeyBackslash => Character('\\', '|', shift),
        KeyComma => Character(',', '<', shift),
        KeyDot => Character('.', '>', shift),
        KeySlash => Character('/', '?', shift),
        KeySpace => Character(' ', ' ', shift),

        // The keypad: its digits are digits here, numlock notwithstanding,
        // which is what a PIN needs.
        96 => new GreeterKey(GreeterKeyAction.Enter),
        79 => Character('1', '1', shift),
        80 => Character('2', '2', shift),
        81 => Character('3', '3', shift),
        75 => Character('4', '4', shift),
        76 => Character('5', '5', shift),
        77 => Character('6', '6', shift),
        71 => Character('7', '7', shift),
        72 => Character('8', '8', shift),
        73 => Character('9', '9', shift),
        82 => Character('0', '0', shift),
        KeyPadDot => Character('.', '.', shift),

        _ => default,
    };

    private static GreeterKey Letter(char lower, bool shift) =>
        new(GreeterKeyAction.Character, shift ? char.ToUpperInvariant(lower) : lower);

    private static GreeterKey Character(char plain, char shifted, bool shift) =>
        new(GreeterKeyAction.Character, shift ? shifted : plain);
}