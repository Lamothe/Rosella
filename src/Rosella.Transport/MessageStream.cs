using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;

namespace Rosella.Transport;

/// <summary>
/// Resolves the wire signature of a message from the receiving side's object
/// map. Throws when the object or opcode is unknown (a protocol violation).
/// </summary>
public delegate IReadOnlyList<ArgumentType> MessageSignatureResolver(uint objectId, ushort opcode);

/// <summary>
/// Accumulates received bytes and descriptors and yields whole decoded
/// messages. Descriptors are consumed in signature order, exactly as the wire
/// format defines them.
/// </summary>
public sealed class MessageStream : IDisposable
{
    private byte[] _buffer = new byte[1024];
    private int _buffered;
    private int[] _descriptors = new int[16];
    private int _descriptorCount;
    private bool _disposed;

    /// <summary>Adds freshly received data and descriptors to the stream.</summary>
    public void Push(ReadOnlySpan<byte> data, ReadOnlySpan<int> descriptors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureDataCapacity(_buffered + data.Length);
        data.CopyTo(_buffer.AsSpan(_buffered));
        _buffered += data.Length;

        EnsureDescriptorCapacity(_descriptorCount + descriptors.Length);
        descriptors.CopyTo(_descriptors.AsSpan(_descriptorCount));
        _descriptorCount += descriptors.Length;
    }

    /// <summary>
    /// Attempts to decode the next complete message. Returns false when more
    /// data is needed; throws <see cref="WireProtocolException"/> when the
    /// stream is malformed.
    /// </summary>
    public bool TryRead(MessageSignatureResolver resolver, out DecodedMessage message)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(resolver);
        message = default;

        if (_buffered < FrameHeader.ByteSize)
        {
            return false;
        }

        var header = FrameHeader.Read(_buffer.AsSpan(0, _buffered));
        if (header.MessageSize < FrameHeader.ByteSize || header.MessageSize % 4 != 0)
        {
            throw new WireProtocolException(
                $"Message size {header.MessageSize} must be a multiple of four and at least {FrameHeader.ByteSize} bytes.");
        }

        if (_buffered < header.MessageSize)
        {
            return false;
        }

        var signature = resolver(header.ObjectId, header.Opcode);
        if (!MessageDecoder.TryDecode(
                _buffer.AsSpan(0, _buffered),
                signature,
                _descriptors.AsSpan(0, _descriptorCount),
                out message,
                out var bytesConsumed,
                out var descriptorsConsumed))
        {
            return false;
        }

        Buffer.BlockCopy(_buffer, bytesConsumed, _buffer, 0, _buffered - bytesConsumed);
        _buffered -= bytesConsumed;

        for (var i = descriptorsConsumed; i < _descriptorCount; i++)
        {
            _descriptors[i - descriptorsConsumed] = _descriptors[i];
        }

        _descriptorCount -= descriptorsConsumed;
        return true;
    }

    /// <summary>Closes any descriptors still buffered in the stream.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        for (var i = 0; i < _descriptorCount; i++)
        {
            using var handle = new SafeFileHandle((IntPtr)_descriptors[i], ownsHandle: true);
        }

        _descriptorCount = 0;
    }

    private void EnsureDataCapacity(int needed)
    {
        if (needed <= _buffer.Length)
        {
            return;
        }

        var capacity = _buffer.Length;
        while (capacity < needed)
        {
            capacity *= 2;
        }

        Array.Resize(ref _buffer, capacity);
    }

    private void EnsureDescriptorCapacity(int needed)
    {
        if (needed <= _descriptors.Length)
        {
            return;
        }

        var capacity = _descriptors.Length;
        while (capacity < needed)
        {
            capacity *= 2;
        }

        Array.Resize(ref _descriptors, capacity);
    }
}
