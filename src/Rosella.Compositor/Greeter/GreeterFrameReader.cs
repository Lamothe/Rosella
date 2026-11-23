using System.Buffers.Binary;
using System.Text;

namespace Rosella.Compositor.Greeter;

/// <summary>
/// Incremental greetd frame reader. A socket read may end anywhere — inside
/// the 4-byte length prefix, inside a payload, or after several whole frames
/// — so bytes are buffered and consumed only when a frame is complete. Extra
/// frames stay in the buffer for the next call instead of being thrown away,
/// which is the difference between a greeter that survives a burst and one
/// that loses the next message.
/// </summary>
internal sealed class GreeterFrameReader
{
    private const int BufferBytes = 16 * 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[BufferBytes];
    private int _start;
    private int _end;
    private int _length = -1;
    private byte[]? _payload;
    private int _payloadRead;

    internal GreeterFrameReader(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Reads exactly one message, blocking until it is complete.</summary>
    internal string Read()
    {
        while (true)
        {
            if (TryTake(out var json))
            {
                return json;
            }

            Fill();
        }
    }

    private bool TryTake(out string json)
    {
        json = string.Empty;

        if (_length < 0)
        {
            if (_end - _start < 4)
            {
                return false;
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_start, 4));
            _start += 4;
            if (length <= 0 || length > GreeterFraming.MaxMessageBytes)
            {
                throw new GreeterProtocolException($"greetd sent a bogus frame length {length}");
            }

            _length = length;
            _payload = new byte[length];
            _payloadRead = 0;
        }

        var available = _end - _start;
        if (available > 0)
        {
            var take = Math.Min(available, _length - _payloadRead);
            _buffer.AsSpan(_start, take).CopyTo(_payload!.AsSpan(_payloadRead, take));
            _start += take;
            _payloadRead += take;
        }

        if (_payloadRead < _length)
        {
            return false;
        }

        json = Encoding.UTF8.GetString(_payload!);
        _length = -1;
        _payload = null;
        _payloadRead = 0;
        return true;
    }

    /// <summary>Compacts the unconsumed bytes, then reads more from the socket.</summary>
    private void Fill()
    {
        if (_start > 0)
        {
            if (_start == _end)
            {
                _start = 0;
                _end = 0;
            }
            else
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
        }

        if (_end == _buffer.Length)
        {
            // Payload bytes are copied out as they arrive, so the buffer can
            // only fill with bytes the parser refused to consume.
            throw new GreeterProtocolException("greetd sent a frame the reader could not consume");
        }

        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read <= 0)
        {
            throw new EndOfStreamException("greetd closed the socket");
        }

        _end += read;
    }
}