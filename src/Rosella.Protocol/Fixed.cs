using System.Globalization;

namespace Rosella.Protocol;

/// <summary>
/// Signed 24.8 fixed-point number: the Wayland 'f' argument type
/// (24 integer bits, 8 fractional bits, stored in one 32-bit word).
/// </summary>
public readonly struct Fixed : IEquatable<Fixed>, IComparable<Fixed>
{
    /// <summary>Number of fractional bits.</summary>
    public const int FractionBits = 8;

    private const int Scale = 1 << FractionBits;

    /// <summary>The raw 24.8 representation.</summary>
    public int RawValue { get; }

    private Fixed(int rawValue)
    {
        RawValue = rawValue;
    }

    /// <summary>Wraps an already encoded 24.8 value.</summary>
    public static Fixed FromRaw(int rawValue)
    {
        return new Fixed(rawValue);
    }

    /// <summary>Converts an integer to 24.8 fixed point.</summary>
    public static Fixed FromInt(int value)
    {
        return new Fixed(checked(value * Scale));
    }

    /// <summary>Converts a double to 24.8 fixed point, rounding halves away from zero.</summary>
    public static Fixed FromDouble(double value)
    {
        var scaled = value * Scale;
        if (double.IsNaN(scaled) || double.IsInfinity(scaled) || scaled < int.MinValue || scaled > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value cannot be represented in 24.8 fixed point.");
        }

        return new Fixed((int)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    public double ToDouble()
    {
        return RawValue / (double)Scale;
    }

    /// <summary>Truncates towards negative infinity, matching <c>wl_fixed_to_int</c>.</summary>
    public int ToIntFloor()
    {
        return RawValue >> FractionBits;
    }

    public static explicit operator double(Fixed value)
    {
        return value.ToDouble();
    }

    public bool Equals(Fixed other)
    {
        return RawValue == other.RawValue;
    }

    public override bool Equals(object? obj)
    {
        return obj is Fixed other && Equals(other);
    }

    public override int GetHashCode()
    {
        return RawValue;
    }

    public int CompareTo(Fixed other)
    {
        return RawValue.CompareTo(other.RawValue);
    }

    public override string ToString()
    {
        return ToDouble().ToString(CultureInfo.InvariantCulture);
    }

    public static bool operator ==(Fixed left, Fixed right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Fixed left, Fixed right)
    {
        return !left.Equals(right);
    }
}
