namespace Rosella.Protocol;

/// <summary>
/// An argument type as it appears on the wire: a <see cref="ArgumentKind"/> plus nullability.
/// </summary>
/// <remarks>
/// Interface names for object/new_id arguments are part of the protocol description,
/// not the wire format, and live in the message layer above this one.
/// </remarks>
public readonly record struct ArgumentType
{
    private ArgumentType(ArgumentKind kind, bool isNullable)
    {
        Kind = kind;
        IsNullable = isNullable;
    }

    public ArgumentKind Kind { get; }

    /// <summary>Whether a null value is permitted ('?' prefix in the signature).</summary>
    public bool IsNullable { get; }

    public static ArgumentType Int32 { get; } = new(ArgumentKind.Int32, false);
    public static ArgumentType UInt32 { get; } = new(ArgumentKind.UInt32, false);
    public static ArgumentType Fixed { get; } = new(ArgumentKind.Fixed, false);
    public static ArgumentType String { get; } = new(ArgumentKind.String, false);
    public static ArgumentType Object { get; } = new(ArgumentKind.Object, false);
    public static ArgumentType NewId { get; } = new(ArgumentKind.NewId, false);
    public static ArgumentType Array { get; } = new(ArgumentKind.Array, false);
    public static ArgumentType Fd { get; } = new(ArgumentKind.Fd, false);

    /// <summary>Returns the nullable variant of this type (only 's' and 'o' can be nullable).</summary>
    public ArgumentType AsNullable()
    {
        if (Kind is not (ArgumentKind.String or ArgumentKind.Object))
        {
            throw new InvalidOperationException($"'{ToSignatureChar()}' arguments cannot be nullable.");
        }

        return new ArgumentType(Kind, true);
    }

    /// <summary>The signature character for this kind ('?' not included).</summary>
    public char ToSignatureChar()
    {
        return Kind switch
        {
            ArgumentKind.Int32 => 'i',
            ArgumentKind.UInt32 => 'u',
            ArgumentKind.Fixed => 'f',
            ArgumentKind.String => 's',
            ArgumentKind.Object => 'o',
            ArgumentKind.NewId => 'n',
            ArgumentKind.Array => 'a',
            ArgumentKind.Fd => 'h',
            _ => throw new InvalidOperationException($"Unhandled argument kind {Kind}."),
        };
    }

    /// <summary>Parses one signature character, with the nullability already stripped.</summary>
    public static ArgumentType FromSignatureChar(char character, bool isNullable)
    {
        ArgumentKind kind;
        switch (character)
        {
            case 'i':
                kind = ArgumentKind.Int32;
                break;
            case 'u':
                kind = ArgumentKind.UInt32;
                break;
            case 'f':
                kind = ArgumentKind.Fixed;
                break;
            case 's':
                kind = ArgumentKind.String;
                break;
            case 'o':
                kind = ArgumentKind.Object;
                break;
            case 'n':
                kind = ArgumentKind.NewId;
                break;
            case 'a':
                kind = ArgumentKind.Array;
                break;
            case 'h':
                kind = ArgumentKind.Fd;
                break;
            default:
                throw new FormatException($"'{character}' is not a Wayland argument type.");
        }

        if (isNullable && kind is not (ArgumentKind.String or ArgumentKind.Object))
        {
            throw new FormatException($"Only string and object arguments can be nullable, not '{character}'.");
        }

        return new ArgumentType(kind, isNullable);
    }
}
