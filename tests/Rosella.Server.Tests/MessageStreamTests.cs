using Rosella.Protocol;
using Rosella.Transport;

namespace Rosella.Server.Tests;

public class MessageStreamTests
{
    [Fact]
    public void TryRead_WaitsForCompleteMessage()
    {
        using var stream = new MessageStream();
        var encoded = MessageEncoder.Encode(1, 0, Signature.Parse("n"), [WireArgument.NewId(2)]);

        stream.Push(encoded.Bytes.AsSpan(0, 6), []);
        Assert.False(stream.TryRead(FixedSignature("n"), out _));

        stream.Push(encoded.Bytes.AsSpan(6), []);
        Assert.True(stream.TryRead(FixedSignature("n"), out var message));
        Assert.Equal(2u, message.Arguments[0].AsNewId());
    }

    [Fact]
    public void TryRead_YieldsMessagesFromOnePush()
    {
        using var stream = new MessageStream();
        var first = MessageEncoder.Encode(1, 0, Signature.Parse(""), []);
        var second = MessageEncoder.Encode(1, 1, Signature.Parse("n"), [WireArgument.NewId(7)]);
        stream.Push(first.Bytes.Concat(second.Bytes).ToArray(), []);

        Assert.True(stream.TryRead(FixedSignature(""), out var one));
        Assert.Equal(0, one.Opcode);
        Assert.True(stream.TryRead(FixedSignature("n"), out var two));
        Assert.Equal(7u, two.Arguments[0].AsNewId());
        Assert.False(stream.TryRead(FixedSignature("n"), out _));
    }

    [Fact]
    public void TryRead_ConsumesDescriptorsInSignatureOrder()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            // Ownership convention: descriptors pushed here are consumed by
            // the test's TryRead calls and closed by the owning handles below;
            // MessageStream.Dispose only closes descriptors still buffered.
            using var stream = new MessageStream();
            using var fileA = File.OpenHandle(Path.Combine(directory, "a"), FileMode.CreateNew, FileAccess.ReadWrite);
            using var fileB = File.OpenHandle(Path.Combine(directory, "b"), FileMode.CreateNew, FileAccess.ReadWrite);
            using var fileC = File.OpenHandle(Path.Combine(directory, "c"), FileMode.CreateNew, FileAccess.ReadWrite);
            var descriptorA = unchecked((int)fileA.DangerousGetHandle());
            var descriptorB = unchecked((int)fileB.DangerousGetHandle());
            var descriptorC = unchecked((int)fileC.DangerousGetHandle());

            var two = MessageEncoder.Encode(
                1,
                0,
                Signature.Parse("hh"),
                [WireArgument.FileDescriptor(descriptorA), WireArgument.FileDescriptor(descriptorB)]);
            var one = MessageEncoder.Encode(1, 0, Signature.Parse("h"), [WireArgument.FileDescriptor(descriptorC)]);
            stream.Push(two.Bytes, two.FileDescriptors.AsSpan());
            stream.Push(one.Bytes, one.FileDescriptors.AsSpan());

            Assert.True(stream.TryRead(FixedSignature("hh"), out var first));
            Assert.Equal(descriptorA, first.Arguments[0].AsFileDescriptor());
            Assert.Equal(descriptorB, first.Arguments[1].AsFileDescriptor());
            Assert.True(stream.TryRead(FixedSignature("h"), out var second));
            Assert.Equal(descriptorC, second.Arguments[0].AsFileDescriptor());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MessageSignatureResolver FixedSignature(string signature)
    {
        var types = Signature.Parse(signature);
        return (_, _) => types;
    }
}
