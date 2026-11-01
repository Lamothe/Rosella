using System.Buffers.Binary;
using System.Text;

namespace Rosella.Compositor.Greeter;

/// <summary>greetd's wire format was violated, or the peer sent nonsense.</summary>
internal sealed class GreeterProtocolException : Exception
{
    internal GreeterProtocolException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// greetd-ipc(7) framing: a 4-byte native-order length followed by that many
/// bytes of UTF-8 JSON. The targets are little-endian, and the reference
/// implementation writes the length in native order, so little-endian is
/// what the socket carries.
///
/// This is deliberately not line-delimited: JSON may contain newlines, and a
/// read may end anywhere — in the middle of the length prefix, in the middle
/// of a payload, or after several whole frames.
/// </summary>
internal static class GreeterFraming
{
    /// <summary>
    /// Largest frame accepted. greetd's messages are a few hundred bytes; a
    /// bogus prefix must not make the greeter allocate the address space.
    /// </summary>
    internal const int MaxMessageBytes = 1 << 20;

    /// <summary>One frame: length prefix then UTF-8 payload.</summary>
    internal static byte[] Encode(string json)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        return frame;
    }

    /// <summary>Writes one frame and flushes it: greetd waits for it now.</summary>
    internal static void Write(Stream stream, string json)
    {
        var frame = Encode(json);
        stream.Write(frame, 0, frame.Length);
        stream.Flush();
    }
}