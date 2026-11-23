namespace Rosella.Protocol;

/// <summary>
/// The primitive argument types defined by the Wayland wire format.
/// </summary>
public enum ArgumentKind
{
    /// <summary>32-bit signed integer ('i').</summary>
    Int32,

    /// <summary>32-bit unsigned integer ('u').</summary>
    UInt32,

    /// <summary>Signed 24.8 fixed-point number ('f').</summary>
    Fixed,

    /// <summary>NUL-terminated UTF-8 string ('s').</summary>
    String,

    /// <summary>32-bit id of an existing protocol object ('o'); 0 is the null object.</summary>
    Object,

    /// <summary>32-bit id of a newly created protocol object ('n'); never 0.</summary>
    NewId,

    /// <summary>Length-prefixed byte array ('a').</summary>
    Array,

    /// <summary>File descriptor passed out-of-band via SCM_RIGHTS ('h').</summary>
    Fd,
}
