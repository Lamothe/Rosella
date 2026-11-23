namespace Rosella.Protocol.Tests;

public class SignatureTests
{
    [Fact]
    public void Parses_WireSignature_IncludingUntypedNewIdExpansion()
    {
        // wl_registry.bind is "un" in the protocol XML but "usun" on the wire:
        // name, then interface string and version, then the new object id.
        var types = Signature.Parse("usun");

        Assert.Equal(
            new[] { ArgumentKind.UInt32, ArgumentKind.String, ArgumentKind.UInt32, ArgumentKind.NewId },
            types.Select(t => t.Kind));
        Assert.All(types, t => Assert.False(t.IsNullable));
    }

    [Fact]
    public void Parses_NullableStringAndObject()
    {
        Assert.True(Signature.Parse("?s").Single().IsNullable);
        Assert.True(Signature.Parse("?o").Single().IsNullable);
    }

    [Fact]
    public void Parses_EmptySignature()
    {
        Assert.Empty(Signature.Parse(string.Empty));
    }

    [Fact]
    public void Rejects_UnknownCharacter()
    {
        Assert.Throws<FormatException>(() => Signature.Parse("uq"));
    }

    [Fact]
    public void Rejects_DanglingNullMarker()
    {
        Assert.Throws<FormatException>(() => Signature.Parse("u?"));
    }

    [Fact]
    public void Rejects_NullableValueTypes()
    {
        Assert.Throws<FormatException>(() => Signature.Parse("?a"));
        Assert.Throws<FormatException>(() => Signature.Parse("?h"));
    }
}
