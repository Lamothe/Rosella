using System.Collections.Immutable;

namespace Rosella.Protocol;

/// <summary>
/// Parses Wayland message signatures (for example <c>"usun"</c>) into wire argument types.
/// </summary>
/// <remarks>
/// This is the wire signature, not the protocol XML signature: an untyped
/// <c>new_id</c> in the XML (such as <c>wl_registry.bind</c>'s <c>"un"</c>) appears
/// on the wire as <c>"usun"</c>, because the interface name string and version
/// are transmitted before the new object id.
/// </remarks>
public static class Signature
{
    /// <summary>Parses a wire signature such as <c>"u?san"</c>.</summary>
    public static ImmutableArray<ArgumentType> Parse(string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);

        var builder = ImmutableArray.CreateBuilder<ArgumentType>(signature.Length);
        var index = 0;
        while (index < signature.Length)
        {
            var isNullable = false;
            if (signature[index] == '?')
            {
                isNullable = true;
                index++;
                if (index == signature.Length)
                {
                    throw new FormatException("Signature ends with '?' but no argument type follows.");
                }
            }

            builder.Add(ArgumentType.FromSignatureChar(signature[index], isNullable));
            index++;
        }

        return builder.ToImmutable();
    }
}
