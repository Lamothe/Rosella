using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Rosella.Pin;

/// <summary>The three outcomes a verifier can report, and nothing else.</summary>
internal enum CryptResult
{
    Match,
    Mismatch,
    Malformed,
}

/// <summary>
/// libxcrypt: yescrypt hashing and verification. Deliberately uses the
/// non-reentrant entry points, because this process verifies one secret once
/// and then exits; that avoids depending on the layout of <c>struct
/// crypt_data</c>, which differs between libxcrypt and glibc's libcrypt.
///
/// Single-threaded only: <c>crypt()</c> returns a pointer to a static buffer.
/// A caller that wants to verify from several threads (a lock screen inside
/// the compositor, say) must use <c>crypt_rn</c> with its own
/// <c>crypt_data</c>, or run the same separate process this tool provides.
/// </summary>
internal static class Crypt
{
    /// <summary>yescrypt: libxcrypt's default, and what pam_unix uses here.</summary>
    internal const string YescryptPrefix = "$y$";

    private const string Library = "libcrypt";
    private const int SaltBytes = 32;
    private const int OutputBytes = 256;

    /// <summary>libxcrypt ships as .so.2; glibc's libcrypt was .so.1.</summary>
    private static readonly string[] Candidates = ["libcrypt.so.2", "libcrypt.so.1"];

    static Crypt()
    {
        NativeLibrary.SetDllImportResolver(typeof(Crypt).Assembly, Resolve);
    }

    [DllImport(Library, EntryPoint = "crypt_gensalt_rn", SetLastError = true)]
    private static extern nint CryptGensaltRn(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string prefix,
        nuint count,
        byte[] rbytes,
        int nrbytes,
        byte[] output,
        int outputSize);

    [DllImport(Library, EntryPoint = "crypt", SetLastError = true)]
    private static extern nint CryptFn(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string phrase,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string setting);

    [DllImport(Library, EntryPoint = "crypt_checksalt", SetLastError = true)]
    private static extern int CryptChecksalt([MarshalAs(UnmanagedType.LPUTF8Str)] string setting);

    /// <summary>
    /// Whether libxcrypt could be loaded at all. Lazy on purpose: a static
    /// field initializer would run before the static constructor installs the
    /// import resolver, and would then resolve "libcrypt" the default way.
    /// </summary>
    internal static bool Available
    {
        get
        {
            _available ??= Probe();
            return _available.Value;
        }
    }

    private static bool? _available;

    /// <summary>Hash a secret with yescrypt and libxcrypt's default cost.</summary>
    internal static string? Hash(string secret, out string? error)
    {
        error = null;
        var entropy = new byte[SaltBytes];
        var output = new byte[OutputBytes];
        try
        {
            RandomNumberGenerator.Fill(entropy);
            var generated = CryptGensaltRn(YescryptPrefix, 0, entropy, entropy.Length, output, output.Length);
            if (generated == 0)
            {
                error = $"crypt_gensalt_rn failed: {Describe(Marshal.GetLastPInvokeError())}";
                return null;
            }

            var salt = Utf8Z(output);
            var result = CryptFn(secret, salt);
            if (result == 0)
            {
                error = $"crypt failed: {Describe(Marshal.GetLastPInvokeError())}";
                return null;
            }

            var hash = Marshal.PtrToStringUTF8(result);
            if (string.IsNullOrEmpty(hash) || hash[0] == '*')
            {
                error = "crypt returned an error marker";
                return null;
            }

            return hash;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    /// <summary>
    /// Verify a secret against a stored modular hash. Comparison is fixed-time
    /// on the full hash string; a malformed stored hash is reported separately
    /// so the caller can fall through to password auth instead of pretending
    /// the secret was wrong.
    /// </summary>
    internal static CryptResult Verify(string secret, string setting, out string? error)
    {
        error = null;
        if (setting.Length is < 10 or > 512 || setting[0] != '$')
        {
            error = "stored hash is not a modular crypt hash";
            return CryptResult.Malformed;
        }

        var saltState = CryptChecksalt(setting);
        if (saltState is 1 or 2)
        {
            error = saltState == 1 ? "stored hash has an invalid salt" : "stored hash uses a disabled method";
            return CryptResult.Malformed;
        }

        var result = CryptFn(secret, setting);
        if (result == 0)
        {
            error = $"crypt failed: {Describe(Marshal.GetLastPInvokeError())}";
            return CryptResult.Malformed;
        }

        var computed = Marshal.PtrToStringUTF8(result);
        if (string.IsNullOrEmpty(computed) || computed[0] == '*')
        {
            error = "stored hash could not be parsed";
            return CryptResult.Malformed;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(setting))
            ? CryptResult.Match
            : CryptResult.Mismatch;
    }

    /// <summary>The hash prefix, for diagnostics: "$y$", "$6$", and so on.</summary>
    internal static string PrefixOf(string setting)
    {
        var end = setting.IndexOf('$', 1);
        return end < 0 ? setting[..Math.Min(setting.Length, 8)] : setting[..(end + 1)];
    }

    /// <summary>libxcrypt's salt verdict: 0 is OK, 1 invalid, 2 disabled, 3 legacy, 4 too cheap.</summary>
    internal static int SaltState(string setting) => CryptChecksalt(setting);

    private static bool Probe()
    {
        try
        {
            // Any call proves the library loaded and the symbols resolved.
            _ = CryptChecksalt("$y$");
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static string Utf8Z(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private static string Describe(int errno) => errno switch
    {
        1 => "EPERM (1)",
        12 => "ENOMEM (12)",
        22 => "EINVAL (22)",
        _ => errno.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(name, Library, StringComparison.Ordinal))
        {
            return 0;
        }

        foreach (var candidate in Candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                return handle;
            }
        }

        return 0;
    }
}