namespace Rosella.Compositor;

/// <summary>wl_shm error codes, as defined by the protocol.</summary>
internal static class ShmErrors
{
    internal const uint InvalidFormat = 0;
    internal const uint InvalidStride = 1;
    internal const uint InvalidFd = 2;
}

/// <summary>A client-side wl_shm validation failure, mapped to wl_shm errors by handlers.</summary>
internal sealed class ShmValidationException : Exception
{
    public ShmValidationException(uint errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public uint ErrorCode { get; }
}
