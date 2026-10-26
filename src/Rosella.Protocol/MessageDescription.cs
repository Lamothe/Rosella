using System.Collections.Immutable;
using System.Text;

namespace Rosella.Protocol;

/// <summary>
/// One request or event of an interface, with its opcode and wire signature.
/// </summary>
/// <remarks>
/// <see cref="Arguments"/> are the XML-level arguments;
/// <see cref="WireSignature"/> is what actually travels on the wire — an
/// untyped <c>new_id</c> expands to interface-name string, version and id
/// (the well-known <c>"usun"</c> quirk of <c>wl_registry.bind</c>).
/// </remarks>
public sealed class MessageDescription
{
    public MessageDescription(string name, ushort opcode, IReadOnlyList<ArgumentDescription> arguments, bool isDestructor = false, uint since = 1)
    {
        Name = name;
        Opcode = opcode;
        Arguments = arguments;
        IsDestructor = isDestructor;
        Since = since;

        var builder = ImmutableArray.CreateBuilder<ArgumentType>();
        foreach (var argument in arguments)
        {
            if (argument.Kind == ArgumentKind.NewId && argument.Interface is null)
            {
                builder.Add(ArgumentType.String);
                builder.Add(ArgumentType.UInt32);
                builder.Add(ArgumentType.NewId);
                continue;
            }

            var type = ArgumentType.FromSignatureChar(KindChar(argument.Kind), argument.AllowNull);
            builder.Add(type);
        }

        WireSignature = builder.ToImmutable();

        var signatureText = new StringBuilder();
        foreach (var type in WireSignature)
        {
            if (type.IsNullable)
            {
                signatureText.Append('?');
            }

            signatureText.Append(type.ToSignatureChar());
        }

        Signature = signatureText.ToString();
    }

    public string Name { get; }

    /// <summary>Position of this message within its interface's request or event list.</summary>
    public ushort Opcode { get; }

    /// <summary>The arguments as declared in the protocol XML.</summary>
    public IReadOnlyList<ArgumentDescription> Arguments { get; }

    /// <summary>Whether this request destroys the target object (type="destructor").</summary>
    public bool IsDestructor { get; }

    /// <summary>The interface version that introduced this message.</summary>
    public uint Since { get; }

    /// <summary>The expanded argument types as encoded on the wire.</summary>
    public ImmutableArray<ArgumentType> WireSignature { get; }

    /// <summary>The wire signature in string form, e.g. <c>"usun"</c>.</summary>
    public string Signature { get; }

    private static char KindChar(ArgumentKind kind)
    {
        return kind switch
        {
            ArgumentKind.Int32 => 'i',
            ArgumentKind.UInt32 => 'u',
            ArgumentKind.Fixed => 'f',
            ArgumentKind.String => 's',
            ArgumentKind.Object => 'o',
            ArgumentKind.NewId => 'n',
            ArgumentKind.Array => 'a',
            ArgumentKind.Fd => 'h',
            _ => throw new InvalidOperationException($"Unhandled argument kind {kind}."),
        };
    }
}
