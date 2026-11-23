namespace Rosella.Protocol.Tests;

public class ArgumentTypeTests
{
    [Fact]
    public void SignatureChars_RoundTrip()
    {
        Assert.Equal('i', ArgumentType.Int32.ToSignatureChar());
        Assert.Equal('u', ArgumentType.UInt32.ToSignatureChar());
        Assert.Equal('f', ArgumentType.Fixed.ToSignatureChar());
        Assert.Equal('s', ArgumentType.String.ToSignatureChar());
        Assert.Equal('o', ArgumentType.Object.ToSignatureChar());
        Assert.Equal('n', ArgumentType.NewId.ToSignatureChar());
        Assert.Equal('a', ArgumentType.Array.ToSignatureChar());
        Assert.Equal('h', ArgumentType.Fd.ToSignatureChar());

        foreach (var character in "iufsonah")
        {
            Assert.Equal(character, ArgumentType.FromSignatureChar(character, false).ToSignatureChar());
        }
    }

    [Fact]
    public void AsNullable_MarksStringAndObjectOnly()
    {
        Assert.True(ArgumentType.String.AsNullable().IsNullable);
        Assert.True(ArgumentType.Object.AsNullable().IsNullable);
        Assert.Throws<InvalidOperationException>(() => ArgumentType.NewId.AsNullable());
        Assert.Throws<InvalidOperationException>(() => ArgumentType.Array.AsNullable());
    }
}
