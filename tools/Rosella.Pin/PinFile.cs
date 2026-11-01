using System.Runtime.InteropServices;
using System.Text;

namespace Rosella.Pin;

/// <summary>
/// The PIN file: one modular crypt hash, root-owned, mode 0600. The hash is
/// the only secret stored, so a reader of the file learns nothing that a
/// brute-force attempt would not also produce.
/// </summary>
internal static class PinFile
{
    /// <summary>Where the PAM helper expects the hash in production.</summary>
    internal const string DefaultPath = "/etc/rosella/pin";

    private const int MaxHashLength = 512;

    private const int OpenReadOnly = 0;
    private const int OpenDirectory = 0x10000;

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEuid();

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);

    /// <summary>Whether this process may write the default path.</summary>
    internal static bool IsPrivileged => GetEuid() == 0;

    /// <summary>
    /// Read and strictly validate the stored hash; null with a reason on any
    /// problem. Strict on purpose: anything that is not exactly one modular
    /// crypt hash must be an error, because a corrupt file and a wrong PIN
    /// have different consequences for the PAM stack.
    /// </summary>
    internal static string? Read(string path, out string? error)
    {
        error = null;
        string text;
        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            error = "no PIN configured";
            return null;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or ArgumentException)
        {
            error = $"cannot read: {exception.Message}";
            return null;
        }

        var hash = text.Trim();
        if (!LooksLikeHash(hash))
        {
            error = "does not contain one modular crypt hash";
            return null;
        }

        return hash;
    }

    /// <summary>
    /// Replace the stored hash atomically: write a uniquely named temporary
    /// file in the same directory, flush it, rename it over the target, pin the
    /// mode, and flush the directory so the rename survives a power loss.
    /// Refuses to follow a symlink at the target.
    /// </summary>
    internal static bool Write(string path, string hash, out string? error)
    {
        error = null;
        string? temporary = null;
        var opened = false;
        try
        {
            var full = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(directory))
            {
                error = $"{path} has no parent directory";
                return false;
            }

            if (IsSymlink(full))
            {
                error = $"refusing to write through the symlink {full}";
                return false;
            }

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            temporary = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            };

            using (var stream = new FileStream(temporary, options))
            {
                opened = true;
                stream.Write(Encoding.UTF8.GetBytes(hash + "\n"));
                stream.Flush(flushToDisk: true);
            }

            // Point of no return: the rename installs the temporary file's own
            // 0600 inode, so nothing after this can fail and mis-report a write
            // that already happened.
            File.Move(temporary, full, overwrite: true);
            SyncDirectory(directory);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or NotSupportedException or ArgumentException)
        {
            error = $"cannot write {path}: {exception.Message}";
            return false;
        }
        finally
        {
            if (opened && temporary is not null)
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>A human-readable report for --check; never includes the hash itself.</summary>
    internal static string Describe(string path, string hash)
    {
        var full = Path.GetFullPath(path);
        UnixFileMode permissions;
        string mode;
        try
        {
            permissions = File.GetUnixFileMode(full);
            mode = ModeString(permissions);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"{full}: mode unavailable ({exception.Message}), {Crypt.PrefixOf(hash)}";
        }

        var state = Crypt.Available ? Crypt.SaltState(hash) : -1;
        var verdict = state switch
        {
            0 => "salt ok",
            1 => "invalid salt",
            2 => "method disabled",
            3 => "legacy method",
            4 => "cost below policy",
            -1 => "salt check unavailable (libxcrypt missing)",
            _ => $"salt state {state}",
        };

        var loose = permissions.HasFlag(UnixFileMode.GroupRead)
            || permissions.HasFlag(UnixFileMode.GroupWrite)
            || permissions.HasFlag(UnixFileMode.OtherRead)
            || permissions.HasFlag(UnixFileMode.OtherWrite);
        return $"{full}: mode {mode}, {Crypt.PrefixOf(hash)}, {verdict}{(loose ? ", WARNING: readable beyond owner" : string.Empty)}";
    }

    /// <summary>
    /// Exactly one modular crypt hash: a $, then only the characters modular
    /// crypt formats use, with at least one further $. Embedded whitespace,
    /// comments and concatenated hashes are all rejected.
    /// </summary>
    private static bool LooksLikeHash(string text)
    {
        if (text.Length is < 10 or > MaxHashLength || text[0] != '$')
        {
            return false;
        }

        foreach (var character in text)
        {
            if (!char.IsAsciiLetterOrDigit(character)
                && character is not ('$' or '.' or '/' or '=' or ',' or '-' or '+' or '_'))
            {
                return false;
            }
        }

        return text.IndexOf('$', 1) > 0;
    }

    private static bool IsSymlink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Cannot tell: treat as a link and refuse rather than risk a write.
            return true;
        }
    }

    /// <summary>
    /// flush the directory entry itself, so the rename is durable. Best effort:
    /// some filesystems reject fsync on a directory, and that is not a reason
    /// to fail a PIN change.
    /// </summary>
    private static void SyncDirectory(string directory)
    {
        try
        {
            var descriptor = Open(directory, OpenReadOnly | OpenDirectory);
            if (descriptor < 0)
            {
                return;
            }

            try
            {
                _ = Fsync(descriptor);
            }
            finally
            {
                _ = Close(descriptor);
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // No libc: the file itself is still flushed.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best effort: the path is a unique temporary name this process made.
        }
    }

    private static string ModeString(UnixFileMode mode)
    {
        var text = string.Create(9, mode, static (span, value) =>
        {
            span[0] = value.HasFlag(UnixFileMode.UserRead) ? 'r' : '-';
            span[1] = value.HasFlag(UnixFileMode.UserWrite) ? 'w' : '-';
            span[2] = value.HasFlag(UnixFileMode.UserExecute) ? 'x' : '-';
            span[3] = value.HasFlag(UnixFileMode.GroupRead) ? 'r' : '-';
            span[4] = value.HasFlag(UnixFileMode.GroupWrite) ? 'w' : '-';
            span[5] = value.HasFlag(UnixFileMode.GroupExecute) ? 'x' : '-';
            span[6] = value.HasFlag(UnixFileMode.OtherRead) ? 'r' : '-';
            span[7] = value.HasFlag(UnixFileMode.OtherWrite) ? 'w' : '-';
            span[8] = value.HasFlag(UnixFileMode.OtherExecute) ? 'x' : '-';
        });
        return text;
    }
}