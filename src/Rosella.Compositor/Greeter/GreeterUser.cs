using System.Globalization;

namespace Rosella.Compositor.Greeter;

/// <summary>
/// The username the login screen fills in by itself, so signing in is just
/// the PIN.
///
/// Two sources, most specific first: an explicit <c>user = name</c> in
/// /etc/rosella/greeter.conf, then the machine's only human account. Anything
/// ambiguous — several human accounts and no config — yields null and the form
/// asks, because guessing which of two people is standing there is worse than
/// one word of typing. The <c>greeter</c> account is never a candidate: it has
/// no login shell.
///
/// The decision is separated from the file reads (<see cref="Resolve"/>) so
/// every rule can be tested without a machine to match.
/// </summary>
internal static class GreeterUser
{
    internal const string DefaultConfigPath = "/etc/rosella/greeter.conf";

    internal const string DefaultPasswdPath = "/etc/passwd";

    /// <summary>First uid of an ordinary account on Fedora (useradd's UID_MIN).</summary>
    private const int FirstHumanUid = 1000;

    /// <summary>Where Fedora's nobody/system range begins.</summary>
    private const int EndHumanUid = 60000;

    /// <summary>Shells that mean "this account cannot sign in".</summary>
    private static readonly string[] NoLoginShells = ["nologin", "false", "sync", "shutdown", "halt"];

    /// <summary>The username to prefill, or null when the form must ask.</summary>
    internal static string? Default() => Resolve(ReadText(DefaultConfigPath), ReadText(DefaultPasswdPath));

    /// <summary>The whole rule, with the file contents passed in.</summary>
    internal static string? Resolve(string? config, string? passwd) =>
        // A `user` key is an instruction: obey it or ask. Falling through to
        // the account list after an unusable value would sign in as someone
        // the config did not name.
        TryUserKey(config, out var configured) ? configured : SingleHumanUser(passwd);

    /// <summary>
    /// The <c>user</c> key from the greeter config. Comments are <c>#</c> to
    /// end of line; unknown keys are ignored so the file can grow. The first
    /// <c>user</c> key wins. True means the key was present, whether or not its
    /// value is usable.
    /// </summary>
    private static bool TryUserKey(string? text, out string? user)
    {
        user = null;
        if (text is null)
        {
            return false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0 || !line[..separator].Trim().Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim();
            user = IsValidName(value) ? value : null;
            return true;
        }

        return false;
    }

    /// <summary>The configured username when the file names a usable one, else null.</summary>
    internal static string? FromConfig(string? text) =>
        TryUserKey(text, out var user) ? user : null;

    /// <summary>
    /// The one account a person could sign in to, or null when there is none
    /// or more than one. Two humans is ambiguous by definition.
    /// </summary>
    internal static string? SingleHumanUser(string? text)
    {
        if (text is null)
        {
            return null;
        }

        string? found = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            // name:passwd:uid:gid:gecos:home:shell
            var fields = line.Split(':');
            if (fields.Length < 7 || !IsValidName(fields[0]))
            {
                continue;
            }

            if (!int.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var uid)
                || uid < FirstHumanUid
                || uid >= EndHumanUid)
            {
                continue;
            }

            var shell = ShellName(fields[6]);
            if (shell.Length == 0 || NoLoginShells.Contains(shell, StringComparer.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = fields[0];
        }

        return found;
    }

    /// <summary>Whether a name can be typed into the form, by the form's own rules.</summary>
    internal static bool IsValidName(string name)
    {
        if (name.Length == 0 || name.Length > GreeterState.MaxUsernameLength)
        {
            return false;
        }

        foreach (var character in name)
        {
            if (character is ' ' or ':' or '/' || char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static string ShellName(string field)
    {
        var shell = field.Trim();
        var slash = shell.LastIndexOf('/');
        return slash < 0 ? shell : shell[(slash + 1)..];
    }

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An unreadable config is not a reason to fail the login screen;
            // the form simply asks for the username as it always did.
            Console.Error.WriteLine($"rosella: greeter: cannot read {path}: {exception.Message}");
            return null;
        }
    }
}