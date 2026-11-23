using System.Text;

namespace Rosella.Protocol;

/// <summary>
/// Strict UTF-8 handling: malformed input or output is an error, never a
/// silent replacement character.
/// </summary>
internal static class WireText
{
    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
