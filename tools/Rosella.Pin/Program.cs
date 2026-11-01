namespace Rosella.Pin;

internal enum Mode
{
    Verify,
    Set,
    Check,
    SelfTest,
    Help,
}

/// <summary>
/// rosella-pin: a PAM helper for a local PIN.
///
///   rosella-pin                      verify the secret on stdin (PAM calls this)
///   rosella-pin --set                set or change the PIN (needs a terminal; root for the default path)
///   rosella-pin --check              report whether a PIN is configured, without revealing it
///   rosella-pin --self-test          exercise hashing, verification, file modes and refusal paths
///
/// Exit codes: 0 match, 1 mismatch, 2 error. PAM treats anything but 0 as a
/// failed module, and the file being unreadable or corrupt must therefore be 2,
/// so the stack can fall through to the password. --check reports 0 for a
/// usable PIN file and 2 for anything else; it never returns 1.
///
/// The secret is only ever read from stdin or a terminal, never from argv, so
/// it cannot leak through ps or shell history.
/// </summary>
internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitMismatch = 1;
    private const int ExitError = 2;

    private const int MinPinLength = 4;
    private const int MaxPinLength = 12;

    private static int Main(string[] args)
    {
        var parsed = Options.Parse(args);
        if (parsed.Error is { } parseError)
        {
            Console.Error.WriteLine($"rosella-pin: {parseError}");
            Console.Error.WriteLine(Options.Usage);
            return ExitError;
        }

        var options = parsed.Options!;
        if (options.Mode == Mode.Help)
        {
            Console.WriteLine(Options.Usage);
            return ExitOk;
        }

        if (options.Mode is Mode.Verify or Mode.Set && !Crypt.Available)
        {
            Console.Error.WriteLine("rosella-pin: libxcrypt is not available (libcrypt.so.2 / libcrypt.so.1)");
            return ExitError;
        }

        try
        {
            return options.Mode switch
            {
                Mode.Set => SetPin(options),
                Mode.Check => Check(options),
                Mode.SelfTest => SelfTest(),
                _ => VerifyPin(options),
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException)
        {
            // Backstop: every path out of Main is 0, 1 or 2, never a stack trace.
            Console.Error.WriteLine($"rosella-pin: {exception.Message}");
            return ExitError;
        }
    }

    private static int VerifyPin(Options options)
    {
        var secret = Prompt.ReadSecret(string.Empty);
        return VerifySecret(options.Path, secret);
    }

    /// <summary>
    /// The whole verification contract in one place, so the self-test exercises
    /// the same exit codes PAM will see.
    /// </summary>
    internal static int VerifySecret(string path, string? secret)
    {
        var hash = PinFile.Read(path, out var readError);
        if (hash is null)
        {
            Console.Error.WriteLine($"rosella-pin: {path}: {readError}");
            return ExitError;
        }

        if (string.IsNullOrEmpty(secret))
        {
            Console.Error.WriteLine("rosella-pin: no secret on stdin");
            return ExitError;
        }

        var result = Crypt.Verify(secret, hash, out var verifyError);
        if (result == CryptResult.Malformed)
        {
            Console.Error.WriteLine($"rosella-pin: {verifyError}");
            return ExitError;
        }

        return result == CryptResult.Match ? ExitOk : ExitMismatch;
    }

    private static int SetPin(Options options)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("rosella-pin: --set needs a terminal, so the PIN is typed twice and never scripted");
            return ExitError;
        }

        if (!PinFile.IsPrivileged && string.Equals(Path.GetFullPath(options.Path), PinFile.DefaultPath, StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"rosella-pin: writing {PinFile.DefaultPath} needs root");
            return ExitError;
        }

        var first = Prompt.ReadSecret("New PIN: ");
        if (first is null)
        {
            return ExitError;
        }

        if (!Validate(first, out var invalid))
        {
            Console.Error.WriteLine($"rosella-pin: {invalid}");
            return ExitError;
        }

        var second = Prompt.ReadSecret("Repeat PIN: ");
        if (!string.Equals(first, second, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("rosella-pin: the PINs do not match");
            return ExitError;
        }

        var (stored, storeError) = Store(first, options.Path);
        if (!stored)
        {
            Console.Error.WriteLine($"rosella-pin: {storeError}");
            return ExitError;
        }

        Console.Error.WriteLine($"rosella-pin: stored {Crypt.PrefixOf(PinFile.Read(options.Path, out _) ?? string.Empty)} PIN in {Path.GetFullPath(options.Path)}");
        return ExitOk;
    }

    /// <summary>
    /// Hash, write, then read the file back and verify against it: a write that
    /// cannot be verified is worse than no write at all.
    /// </summary>
    internal static (bool Stored, string? Error) Store(string pin, string path)
    {
        var hash = Crypt.Hash(pin, out var hashError);
        if (hash is null)
        {
            return (false, hashError);
        }

        if (!PinFile.Write(path, hash, out var writeError))
        {
            return (false, writeError);
        }

        var readBack = PinFile.Read(path, out var readError);
        if (readBack is null || Crypt.Verify(pin, readBack, out _) != CryptResult.Match)
        {
            return (false, $"wrote {path} but could not verify it ({readError ?? "hash mismatch"})");
        }

        return (true, null);
    }

    private static int Check(Options options)
    {
        var hash = PinFile.Read(options.Path, out var error);
        if (hash is null)
        {
            Console.WriteLine($"{Path.GetFullPath(options.Path)}: {error}");
            return ExitError;
        }

        Console.WriteLine(PinFile.Describe(options.Path, hash));
        return CheckExit(hash);
    }

    /// <summary>
    /// Whether a stored hash is usable at all. States 3 (legacy) and 4 (too
    /// cheap) still authenticate, so they are usable; only an invalid salt or
    /// a disabled method is not.
    /// </summary>
    internal static int CheckExit(string hash)
    {
        if (!Crypt.Available)
        {
            return ExitError;
        }

        return Crypt.SaltState(hash) is 0 or 3 or 4 ? ExitOk : ExitError;
    }

    /// <summary>
    /// Four to twelve digits. Four is allowed because it is the shortest PIN
    /// the user asked for; it is only defensible with a failure lockout, which
    /// the PAM stack provides (pam_faillock), so never deploy this without it.
    /// </summary>
    internal static bool Validate(string pin, out string? error)
    {
        error = null;
        if (pin.Length is < MinPinLength or > MaxPinLength)
        {
            error = $"a PIN must be {MinPinLength} to {MaxPinLength} digits";
            return false;
        }

        foreach (var character in pin)
        {
            if (!char.IsAsciiDigit(character))
            {
                error = "a PIN must contain digits only";
                return false;
            }
        }

        return true;
    }

    private static int SelfTest()
    {
        var failures = 0;
        var checks = 0;
        var directory = Path.Combine(Path.GetTempPath(), $"rosella-pin-{Guid.NewGuid():N}");

        void Expect(string name, bool condition)
        {
            checks++;
            if (!condition)
            {
                failures++;
            }

            Console.WriteLine($"  {(condition ? "pass" : "FAIL")}  {name}");
        }

        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "pin");

            const string Secret = "481516";
            var hash = Crypt.Hash(Secret, out var hashError);
            Expect($"yescrypt hash generated ({hashError ?? "no error"})",
                hash is not null && hash.StartsWith(Crypt.YescryptPrefix, StringComparison.Ordinal));
            if (hash is null)
            {
                return ExitError;
            }

            Expect("salt state is ok", Crypt.SaltState(hash) == 0);
            Expect("hash verifies", Crypt.Verify(Secret, hash, out _) == CryptResult.Match);
            Expect("wrong secret mismatches", Crypt.Verify("481517", hash, out _) == CryptResult.Mismatch);
            Expect("longer secret mismatches", Crypt.Verify(Secret + "0", hash, out _) == CryptResult.Mismatch);
            Expect("truncated hash is malformed", Crypt.Verify(Secret, "$y$", out _) == CryptResult.Malformed);
            Expect("plain text is malformed", Crypt.Verify(Secret, "481516", out _) == CryptResult.Malformed);
            Expect("empty secret mismatches", Crypt.Verify(string.Empty, hash, out _) == CryptResult.Mismatch);

            var (stored, storeError) = Store(Secret, path);
            Expect($"store writes and verifies ({storeError ?? "no error"})", stored);
            var fileMode = File.GetUnixFileMode(path);
            Expect($"file mode is 0600 ({fileMode})",
                fileMode == (UnixFileMode.UserRead | UnixFileMode.UserWrite));
            var readBack = PinFile.Read(path, out _);
            Expect("read returns a hash that verifies",
                readBack is not null && Crypt.Verify(Secret, readBack, out _) == CryptResult.Match);
            Expect("describe names the format", PinFile.Describe(path, hash).Contains("$y$", StringComparison.Ordinal));
            Expect("describe reports a good salt", PinFile.Describe(path, hash).Contains("salt ok", StringComparison.Ordinal));

            Expect("verify: correct secret is 0", VerifySecret(path, Secret) == ExitOk);
            Expect("verify: wrong secret is 1", VerifySecret(path, "481517") == ExitMismatch);
            Expect("verify: missing secret is 2", VerifySecret(path, null) == ExitError);
            Expect("verify: empty secret is 2", VerifySecret(path, string.Empty) == ExitError);
            Expect("verify: missing file is 2", VerifySecret(Path.Combine(directory, "absent"), Secret) == ExitError);

            File.WriteAllText(path, "not a hash\n");
            Expect("corrupt file is 2", VerifySecret(path, Secret) == ExitError);
            File.WriteAllText(path, hash + "\n" + hash + "\n");
            Expect("two hashes are 2", VerifySecret(path, Secret) == ExitError);
            File.WriteAllText(path, hash + "\n# comment\n");
            Expect("hash plus comment is 2", VerifySecret(path, Secret) == ExitError);
            File.WriteAllText(path, "$y$abc def\n");
            Expect("embedded whitespace is 2", VerifySecret(path, Secret) == ExitError);
            File.WriteAllText(path, string.Empty);
            Expect("empty file is 2", VerifySecret(path, Secret) == ExitError);

            var loose = Path.Combine(directory, "loose");
            File.WriteAllText(loose, hash + "\n");
            File.SetUnixFileMode(loose, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
            Expect("loose mode is re-pinned by a write", PinFile.Write(loose, hash, out _)
                && File.GetUnixFileMode(loose) == (UnixFileMode.UserRead | UnixFileMode.UserWrite));

            var linkTarget = Path.Combine(directory, "target");
            File.WriteAllText(linkTarget, hash + "\n");
            var link = Path.Combine(directory, "link");
            File.CreateSymbolicLink(link, linkTarget);
            Expect("symlink target is refused", !PinFile.Write(link, hash, out _));

            var dangling = Path.Combine(directory, "dangling");
            File.CreateSymbolicLink(dangling, Path.Combine(directory, "nowhere"));
            Expect("dangling symlink is refused", !PinFile.Write(dangling, hash, out _));

            var directoryLink = Path.Combine(directory, "dirlink");
            File.CreateSymbolicLink(directoryLink, directory);
            Expect("symlink to a directory is refused", !PinFile.Write(directoryLink, hash, out _));

            var nested = Path.Combine(directory, "nested", "pin");
            Expect("missing directory is created", PinFile.Write(nested, hash, out _));
            var nestedMode = File.GetUnixFileMode(Path.GetDirectoryName(nested)!);
            Expect($"created directory is 0755 ({nestedMode})",
                nestedMode == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute));

            Expect("three digits is rejected", !Validate("123", out _));
            Expect("four digits is accepted", Validate("1234", out _));
            Expect("too long is rejected", !Validate("1234567890123", out _));
            Expect("non-digit is rejected", !Validate("1234a", out _));
            Expect("twelve digits is accepted", Validate("123456789012", out _));

            // Real hashes, produced by openssl and libxcrypt, not by this tool.
            const string Sha512 = "$6$abcdefgh$Z7KfoKnKTSZrzo5VZ0YubGLQOj9ov6sHo9TmE3zIU/LHKhpE30zCnZ0mcIXYf9r9rQ4DYaXoxAFSPFlcWdxjB.";
            const string Sha256 = "$5$abcdefgh$OcHWp0Vj4l19dnE2g4tHD4hbItEZgO0bjRV6LjwCLoA";
            const string Bcrypt = "$2b$12$abcdefghijklmnopqrstuu";
            Expect("$y$ is usable", CheckExit(hash) == ExitOk);
            Expect("$6$ is usable", CheckExit(Sha512) == ExitOk);
            Expect("$5$ legacy is usable", CheckExit(Sha256) == ExitOk);
            Expect("$2b$ is usable", CheckExit(Bcrypt) == ExitOk);
            Expect("unknown prefix is unusable", CheckExit("$9$abcdefgh$xyz") == ExitError);
            File.WriteAllText(Path.Combine(directory, "sha512"), Sha512 + "\n");
            var sha512Path = Path.Combine(directory, "sha512");
            File.SetUnixFileMode(sha512Path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Expect("$6$ fixture verifies a mismatched secret", VerifySecret(sha512Path, "nope") == ExitMismatch);

            var help = Options.Parse(["--help"]);
            Expect("--help is not an error", help.Error is null && help.Options?.Mode == Mode.Help);
            Expect("unknown argument is an error", Options.Parse(["--nope"]).Error is not null);
            Expect("--path without a value is an error", Options.Parse(["--path"]).Error is not null);
            Expect("default mode is verify", Options.Parse([]).Options?.Mode == Mode.Verify);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            failures++;
            Console.WriteLine($"  FAIL  self-test aborted: {exception.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"  warning: could not remove {directory}: {exception.Message}");
            }
        }

        Console.WriteLine($"self-test: {checks - failures}/{checks} passed, libxcrypt {(Crypt.Available ? "present" : "MISSING")}");
        return failures == 0 ? ExitOk : ExitError;
    }

    private sealed record Options(Mode Mode, string Path)
    {
        internal const string Usage =
            """
            usage: rosella-pin [--set | --check | --self-test | --help] [--path FILE]

              (no mode)     read a secret from stdin and verify it against FILE
              --set         prompt twice and store a new 4-12 digit PIN
              --check       report the file's state without revealing the hash
              --self-test   exercise hashing, verification and the refusal paths
              --path FILE   use FILE instead of /etc/rosella/pin

            exit: 0 match (or usable, for --check), 1 mismatch, 2 error
            """;

        internal static (Options? Options, string? Error) Parse(string[] args)
        {
            var mode = Mode.Verify;
            var path = PinFile.DefaultPath;
            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--set":
                        mode = Mode.Set;
                        break;
                    case "--check":
                        mode = Mode.Check;
                        break;
                    case "--self-test":
                        mode = Mode.SelfTest;
                        break;
                    case "--help" or "-h":
                        return (new Options(Mode.Help, path), null);
                    case "--path":
                        if (index + 1 >= args.Length)
                        {
                            return (null, "--path needs a file name");
                        }

                        path = args[++index];
                        break;
                    default:
                        return (null, $"unknown argument '{args[index]}' (use --help)");
                }
            }

            return (new Options(mode, path), null);
        }
    }
}