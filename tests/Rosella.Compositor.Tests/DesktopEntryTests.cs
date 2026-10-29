namespace Rosella.Compositor.Tests;

/// <summary>
/// The .desktop parsing the taskbar launchers rely on: the [Desktop
/// Entry] group only, the keys the bar needs, NoDisplay/Hidden skipping,
/// and Exec → argv with GLib quoting and field codes.
/// </summary>
public class DesktopEntryTests
{
    [Fact]
    public void Parse_ReadsTheDesktopEntryKeys()
    {
        var entry = DesktopEntry.Parse(
            """
            [Desktop Entry]
            Type=Application
            Name=Google Chrome
            Exec=/usr/bin/google-chrome-stable %U
            Icon=google-chrome
            StartupWMClass=google-chrome
            """,
            "google-chrome.desktop");

        Assert.NotNull(entry);
        Assert.Equal("Google Chrome", entry.Name);
        Assert.Equal("/usr/bin/google-chrome-stable %U", entry.Exec);
        Assert.Equal("google-chrome", entry.Icon);
        Assert.Equal("google-chrome", entry.StartupWMClass);
        Assert.False(entry.IsHidden);
    }

    [Fact]
    public void Parse_IgnoresOtherGroupsAndDuplicateKeys()
    {
        var entry = DesktopEntry.Parse(
            """
            [Desktop Entry]
            Name=First
            Exec=first
            [Desktop Action new-window]
            Name=Second
            Exec=second
            Icon=other
            """,
            "app.desktop");

        Assert.NotNull(entry);
        Assert.Equal("First", entry.Name);
        Assert.Equal("first", entry.Exec);
        Assert.Equal(string.Empty, entry.Icon);
    }

    [Fact]
    public void Parse_SkipsEntriesMarkedNoDisplayOrHidden()
    {
        Assert.True(DesktopEntry.Parse("[Desktop Entry]\nName=A\nExec=a\nNoDisplay=true\n", "a.desktop")!.IsHidden);
        Assert.True(DesktopEntry.Parse("[Desktop Entry]\nName=A\nExec=a\nHidden=TRUE\n", "a.desktop")!.IsHidden);
        Assert.False(DesktopEntry.Parse("[Desktop Entry]\nName=A\nExec=a\nNoDisplay=false\nHidden=false\n", "a.desktop")!.IsHidden);
    }

    [Fact]
    public void ParseExec_SplitsUnquotedWordsOnWhitespace()
    {
        Assert.Equal(["nautilus", "--new-window"], DesktopEntry.ParseExec("  nautilus   --new-window  ")!);
    }

    [Fact]
    public void ParseExec_HandlesGlibQuoting()
    {
        // Double quotes group with backslash escapes, single quotes are
        // literal, a bare backslash escapes the next character.
        Assert.Equal(
            ["/opt/My App/app", "two words", "single \" inner", "escaped space", "tail"],
            DesktopEntry.ParseExec("\"/opt/My App/app\" \"two words\" 'single \" inner' escaped\\ space tail")!);
    }

    [Fact]
    public void ParseExec_InsideDoubleQuotesBackslashesStayLiteralBeforeOrdinaryCharacters()
    {
        // Wine-style Exec: \P, \A and \a are not in the reserved escape
        // set (", `, $, \ and newline), so inside double quotes the
        // backslashes must survive intact.
        Assert.Equal(
            ["wine", @"C:\Program Files\App\app.exe"],
            DesktopEntry.ParseExec("wine \"C:\\Program Files\\App\\app.exe\"")!);
    }

    [Fact]
    public void ParseExec_InsideDoubleQuotesBackslashesEscapeOnlyReservedCharacters()
    {
        Assert.Equal(["a\"b"], DesktopEntry.ParseExec("\"a\\\"b\"")!); // \" → "
        Assert.Equal(["a\\b"], DesktopEntry.ParseExec("\"a\\\\b\"")!); // \\ → \
        Assert.Equal(["a$b"], DesktopEntry.ParseExec("\"a\\$b\"")!); // \$ → $
        Assert.Equal(["a`b"], DesktopEntry.ParseExec("\"a\\`b\"")!); // \` → `
    }

    [Fact]
    public void ParseExec_KeepsQuotedEmptyArguments()
    {
        Assert.Equal(["app", ""], DesktopEntry.ParseExec("app \"\"")!);
        Assert.Equal(["app", "", "tail"], DesktopEntry.ParseExec("app '' tail")!);
    }

    [Fact]
    public void ParseExec_DropsWordsThatBecameEmptyThroughFieldCodeStripping()
    {
        Assert.Equal(["app"], DesktopEntry.ParseExec("app %U")!);
        Assert.Equal(["app"], DesktopEntry.ParseExec("app %f%c")!);
    }

    [Fact]
    public void ParseExec_RejectsMalformedQuoting()
    {
        Assert.Null(DesktopEntry.ParseExec("app \"unterminated"));
        Assert.Null(DesktopEntry.ParseExec("app 'unterminated"));
        Assert.Null(DesktopEntry.ParseExec("app trailing\\"));
        Assert.Null(DesktopEntry.ParseExec("   "));
    }

    [Fact]
    public void ParseExec_StripsFieldCodes()
    {
        Assert.Equal(["app"], DesktopEntry.ParseExec("app %U")!);
        Assert.Equal(["app"], DesktopEntry.ParseExec("app %u %F %f %i %c %k %d %D %n %N %v %m")!);
        Assert.Equal(["app", "--file=kept"], DesktopEntry.ParseExec("app %f --file=kept")!);

        // Field codes vanish wherever they sit, even inside quotes.
        Assert.Equal(["echo"], DesktopEntry.ParseExec("echo '%u'")!);
    }

    [Fact]
    public void ParseExec_UnescapesDoublePercent()
    {
        Assert.Equal(["app", "100%"], DesktopEntry.ParseExec("app 100%%")!);
        Assert.Equal(["app", "50%", "x"], DesktopEntry.ParseExec("app 50%% %u x")!);

        // An unknown or trailing per cent sign stays literal.
        Assert.Equal(["app", "a%zb"], DesktopEntry.ParseExec("app a%zb")!);
        Assert.Equal(["app", "end%"], DesktopEntry.ParseExec("app end%")!);
    }
}
