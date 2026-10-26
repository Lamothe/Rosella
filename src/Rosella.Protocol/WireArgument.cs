namespace Rosella.Protocol;

/// <summary>
/// A single argument value as transmitted on the wire.
/// </summary>
public readonly struct WireArgument : IEquatable<WireArgument>
{
    private readonly int _signed;
    private readonly uint _unsigned;
    private readonly string? _text;
    private readonly byte[]? _bytes;

    private WireArgument(ArgumentKind kind, int signed, uint unsigned, string? text, byte[]? bytes)
    {
        Kind = kind;
        _signed = signed;
        _unsigned = unsigned;
        _text = text;
        _bytes = bytes;
    }

    public ArgumentKind Kind { get; }

    /// <summary>Creates an 'i' argument.</summary>
    public static WireArgument Int32(int value)
    {
        return new WireArgument(ArgumentKind.Int32, value, 0, null, null);
    }

    /// <summary>Creates a 'u' argument.</summary>
    public static WireArgument UInt32(uint value)
    {
        return new WireArgument(ArgumentKind.UInt32, 0, value, null, null);
    }

    /// <summary>Creates an 'f' argument.</summary>
    public static WireArgument FixedPoint(Fixed value)
    {
        return new WireArgument(ArgumentKind.Fixed, value.RawValue, 0, null, null);
    }

    /// <summary>Creates a non-null 's' argument.</summary>
    public static WireArgument String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new WireArgument(ArgumentKind.String, 0, 0, value, null);
    }

    /// <summary>Creates a null 's' argument (encoded as length 0).</summary>
    public static WireArgument NullString()
    {
        return new WireArgument(ArgumentKind.String, 0, 0, null, null);
    }

    /// <summary>Creates an 'o' argument; 0 is the null object.</summary>
    public static WireArgument Object(uint id)
    {
        return new WireArgument(ArgumentKind.Object, 0, id, null, null);
    }

    /// <summary>Creates an 'n' argument; the id must not be 0.</summary>
    public static WireArgument NewId(uint id)
    {
        if (id == 0)
        {
            throw new ArgumentException("Object id 0 is reserved and cannot be a new_id.", nameof(id));
        }

        return new WireArgument(ArgumentKind.NewId, 0, id, null, null);
    }

    /// <summary>Creates an 'a' argument from a defensive copy of <paramref name="bytes"/>.</summary>
    public static WireArgument Array(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return new WireArgument(ArgumentKind.Array, 0, 0, null, (byte[])bytes.Clone());
    }

    /// <summary>Creates an 'h' argument.</summary>
    public static WireArgument FileDescriptor(int descriptor)
    {
        if (descriptor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(descriptor), descriptor, "File descriptors must be non-negative.");
        }

        return new WireArgument(ArgumentKind.Fd, descriptor, 0, null, null);
    }

    public int AsInt32()
    {
        return Kind == ArgumentKind.Int32 ? _signed : throw WrongKind(ArgumentKind.Int32);
    }

    public uint AsUInt32()
    {
        return Kind == ArgumentKind.UInt32 ? _unsigned : throw WrongKind(ArgumentKind.UInt32);
    }

    public Fixed AsFixed()
    {
        return Kind == ArgumentKind.Fixed ? Fixed.FromRaw(_signed) : throw WrongKind(ArgumentKind.Fixed);
    }

    /// <summary>Returns the string value, which must not be null.</summary>
    public string AsString()
    {
        return AsNullableString() ?? throw new InvalidOperationException("String argument is null.");
    }

    /// <summary>Returns the string value, or <see langword="null"/> for a null string.</summary>
    public string? AsNullableString()
    {
        return Kind == ArgumentKind.String ? _text : throw WrongKind(ArgumentKind.String);
    }

    /// <summary>Returns the object id; 0 is the null object.</summary>
    public uint AsObject()
    {
        return Kind == ArgumentKind.Object ? _unsigned : throw WrongKind(ArgumentKind.Object);
    }

    public uint AsNewId()
    {
        return Kind == ArgumentKind.NewId ? _unsigned : throw WrongKind(ArgumentKind.NewId);
    }

    public ReadOnlyMemory<byte> AsArray()
    {
        return Kind == ArgumentKind.Array ? _bytes ?? [] : throw WrongKind(ArgumentKind.Array);
    }

    public int AsFileDescriptor()
    {
        return Kind == ArgumentKind.Fd ? _signed : throw WrongKind(ArgumentKind.Fd);
    }

    public bool Equals(WireArgument other)
    {
        if (Kind != other.Kind)
        {
            return false;
        }

        switch (Kind)
        {
            case ArgumentKind.Int32:
            case ArgumentKind.Fixed:
            case ArgumentKind.Fd:
                return _signed == other._signed;
            case ArgumentKind.UInt32:
            case ArgumentKind.Object:
            case ArgumentKind.NewId:
                return _unsigned == other._unsigned;
            case ArgumentKind.String:
                return string.Equals(_text, other._text, StringComparison.Ordinal);
            case ArgumentKind.Array:
                return AsArray().Span.SequenceEqual(other.AsArray().Span);
            default:
                return false;
        }
    }

    public override bool Equals(object? obj)
    {
        return obj is WireArgument other && Equals(other);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Kind);
        switch (Kind)
        {
            case ArgumentKind.Int32:
            case ArgumentKind.Fixed:
            case ArgumentKind.Fd:
                hash.Add(_signed);
                break;
            case ArgumentKind.String:
                hash.Add(_text, StringComparer.Ordinal);
                break;
            case ArgumentKind.Array:
                hash.AddBytes(_bytes ?? []);
                break;
            default:
                hash.Add(_unsigned);
                break;
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        return Kind switch
        {
            ArgumentKind.Int32 => $"i:{AsInt32()}",
            ArgumentKind.UInt32 => $"u:{AsUInt32()}",
            ArgumentKind.Fixed => $"f:{AsFixed()}",
            ArgumentKind.String => _text is null ? "s:null" : $"s:\"{_text}\"",
            ArgumentKind.Object => $"o:{AsObject()}",
            ArgumentKind.NewId => $"n:{AsNewId()}",
            ArgumentKind.Array => $"a:{AsArray().Length} bytes",
            ArgumentKind.Fd => $"h:{AsFileDescriptor()}",
            _ => Kind.ToString(),
        };
    }

    private InvalidOperationException WrongKind(ArgumentKind expected)
    {
        return new InvalidOperationException($"Argument is {Kind}, not {expected}.");
    }
}
