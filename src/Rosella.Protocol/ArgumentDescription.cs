namespace Rosella.Protocol;

/// <summary>
/// One argument as declared in the protocol XML: its kind, nullability and,
/// for object/new_id arguments, the interface it refers to (when known at
/// protocol-definition time).
/// </summary>
public sealed class ArgumentDescription
{
    public ArgumentDescription(string name, ArgumentKind kind, bool allowNull, string? interfaceName)
    {
        Name = name;
        Kind = kind;
        AllowNull = allowNull;
        Interface = interfaceName;
    }

    public string Name { get; }

    public ArgumentKind Kind { get; }

    public bool AllowNull { get; }

    /// <summary>Interface of an object/new_id argument, or null when untyped.</summary>
    public string? Interface { get; }
}
