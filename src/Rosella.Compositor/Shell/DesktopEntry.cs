using System.Text;

namespace Rosella.Compositor;

/// <summary>
/// A parsed XDG desktop entry: only the keys the taskbar launchers need,
/// from the [Desktop Entry] group only. Values are kept exactly as the
/// file wrote them (the keys we read carry no key-file escapes in
/// practice); the Exec value gets its own GLib-style argv parsing.
/// </summary>
internal sealed record DesktopEntry(string Id, string Name, string Exec, string Icon, string StartupWMClass, bool NoDisplay, bool Hidden)
{
    /// <summary>Entries the spec says never to show: NoDisplay or Hidden.</summary>
    internal bool IsHidden => NoDisplay || Hidden;

    /// <summary>Parses a desktop file from disk. Null when unreadable or malformed.</summary>
    internal static DesktopEntry? ParseFile(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path), Path.GetFileName(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"desktop-entry: {path}: {exception.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses the [Desktop Entry] group out of the file text. Only Name,
    /// Exec, Icon, StartupWMClass, NoDisplay and Hidden are read; groups
    /// after [Desktop Entry] (desktop actions, translations) are ignored
    /// entirely, and the first occurrence of a key wins.
    /// </summary>
    internal static DesktopEntry? Parse(string text, string id)
    {
        var name = string.Empty;
        var exec = string.Empty;
        var icon = string.Empty;
        var startupWmClass = string.Empty;
        var noDisplay = false;
        var hidden = false;
        var seenName = false;
        var seenExec = false;
        var seenIcon = false;
        var seenClass = false;
        var seenNoDisplay = false;
        var seenHidden = false;
        var inGroup = false;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line[0] == '[')
            {
                inGroup = line == "[Desktop Entry]";
                continue;
            }

            if (!inGroup)
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator];
            var value = line[(separator + 1)..];
            switch (key)
            {
                case "Name" when !seenName:
                    name = value;
                    seenName = true;
                    break;

                case "Exec" when !seenExec:
                    exec = value;
                    seenExec = true;
                    break;

                case "Icon" when !seenIcon:
                    icon = value;
                    seenIcon = true;
                    break;

                case "StartupWMClass" when !seenClass:
                    startupWmClass = value;
                    seenClass = true;
                    break;

                case "NoDisplay" when !seenNoDisplay:
                    noDisplay = IsTrue(value);
                    seenNoDisplay = true;
                    break;

                case "Hidden" when !seenHidden:
                    hidden = IsTrue(value);
                    seenHidden = true;
                    break;
            }
        }

        return new DesktopEntry(id, name, exec, icon, startupWmClass, noDisplay, hidden);
    }

    /// <summary>
    /// Turns an Exec value into an argv, GLib style: words split on
    /// whitespace outside quotes, single quotes are literal, and inside
    /// double quotes a backslash escapes only the reserved set — `"`, `` ` ``,
    /// `$`, `\` and the newline — while before any other character it stays
    /// literal (g_shell_parse_argv semantics, so Wine paths like
    /// "C:\Program Files\App\app.exe" survive). A bare backslash escapes
    /// the next character. Field codes (%U %u %F %f %i %c %k %d %D %n %N
    /// %v %m) are stripped — the launcher opens no files — and %% unescapes
    /// to a literal %. Words that were empty from quoting stay; words that
    /// became empty through field-code stripping go. Returns null when the
    /// quoting is malformed.
    /// </summary>
    internal static string[]? ParseExec(string exec)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var inWord = false;
        var index = 0;
        while (index < exec.Length)
        {
            var character = exec[index];
            if (char.IsWhiteSpace(character))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }

                index++;
                continue;
            }

            inWord = true;
            switch (character)
            {
                case '\\' when index + 1 < exec.Length:
                    word.Append(exec[index + 1]);
                    index += 2;
                    continue;

                case '\\':
                    return null; // dangling escape

                case '\'':
                    var singleEnd = exec.IndexOf('\'', index + 1);
                    if (singleEnd < 0)
                    {
                        return null;
                    }

                    word.Append(exec, index + 1, (singleEnd - index) - 1);
                    index = singleEnd + 1;
                    continue;

                case '"':
                    index++;
                    while (true)
                    {
                        if (index >= exec.Length)
                        {
                            return null; // unterminated
                        }

                        var inner = exec[index];
                        if (inner == '"')
                        {
                            index++;
                            break;
                        }

                        if (inner == '\\')
                        {
                            if (index + 1 >= exec.Length)
                            {
                                return null; // dangling escape
                            }

                            // Inside double quotes a backslash escapes only
                            // the reserved set (g_shell_parse_argv): ", `, $,
                            // \ and the newline, which is swallowed with the
                            // backslash. Before any other character the
                            // backslash stays literal, so Wine paths like
                            // "C:\Program Files\App\app.exe" survive intact.
                            var escaped = exec[index + 1];
                            if (escaped is '"' or '`' or '$' or '\\')
                            {
                                word.Append(escaped);
                            }
                            else if (escaped != '\n')
                            {
                                word.Append('\\');
                                word.Append(escaped);
                            }

                            index += 2;
                            continue;
                        }

                        word.Append(inner);
                        index++;
                    }

                    continue;

                default:
                    word.Append(character);
                    index++;
                    continue;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        // Field codes vanish; a word that BECAME empty through stripping is
        // dropped (a field-code-only word carries no argument), but a word
        // that was empty all along — a quoted "" — is a real empty argument
        // and stays. argv[0] is required.
        var argv = new List<string>();
        foreach (var candidate in words)
        {
            var (text, removedFieldCode) = StripFieldCodes(candidate);
            if (text.Length > 0 || !removedFieldCode)
            {
                argv.Add(text);
            }
        }

        return argv.Count == 0 ? null : [.. argv];
    }

    /// <summary>
    /// Removes field codes from one argv word; %% becomes a literal %. The
    /// flag reports whether a field code actually vanished, so callers can
    /// tell a word that became empty through stripping from one that was
    /// empty from quoting.
    /// </summary>
    private static (string Text, bool RemovedFieldCode) StripFieldCodes(string word)
    {
        if (!word.Contains('%'))
        {
            return (word, false);
        }

        var stripped = new StringBuilder(word.Length);
        var removedFieldCode = false;
        var index = 0;
        while (index < word.Length)
        {
            var character = word[index];
            if (character != '%' || index + 1 >= word.Length)
            {
                stripped.Append(character);
                index++;
                continue;
            }

            var code = word[index + 1];
            if (code == '%')
            {
                stripped.Append('%');
                index += 2;
                continue;
            }

            if (code is 'U' or 'u' or 'F' or 'f' or 'i' or 'c' or 'k' or 'd' or 'D' or 'n' or 'N' or 'v' or 'm')
            {
                removedFieldCode = true;
                index += 2;
                continue;
            }

            // Unknown code: keep the per cent sign literally.
            stripped.Append('%');
            index++;
        }

        return (stripped.ToString(), removedFieldCode);
    }

    private static bool IsTrue(string value) => value.Trim().ToLowerInvariant() is "true" or "1";
}
