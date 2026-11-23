using Rosella.Compositor.Greeter;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The login screen's prefilled username. The rule is separated from the file
/// reads so each case is pinned: an explicit config wins, a lone human account
/// is used, and anything ambiguous asks rather than guesses.
/// </summary>
public class GreeterUserTests
{
    private const string SingleUser = """
root:x:0:0:root:/root:/bin/bash
bin:x:1:1:bin:/bin:/sbin/nologin
greeter:x:963:963::/:/sbin/nologin
michael:x:1000:1000:Michael Lamothe:/home/michael:/bin/bash
nobody:x:65534:65534:Nobody:/:/sbin/nologin
""";

    private const string TwoUsers = """
root:x:0:0:root:/root:/bin/bash
michael:x:1000:1000:Michael Lamothe:/home/michael:/bin/bash
alice:x:1001:1001:Alice:/home/alice:/bin/bash
""";

    [Fact]
    public void Resolve_UsesTheOnlyHumanAccount()
    {
        Assert.Equal("michael", GreeterUser.Resolve(null, SingleUser));
    }

    [Fact]
    public void Resolve_IgnoresSystemAndNoLoginAccounts()
    {
        // root, bin, the greeter itself and nobody: real /etc/passwd entries
        // that must never be filled in, because none of them is a person who
        // can sign in to a desktop.
        const string passwd = """
root:x:0:0:root:/root:/bin/bash
bin:x:1:1:bin:/bin:/sbin/nologin
greeter:x:963:963::/:/sbin/nologin
nobody:x:65534:65534:Nobody:/:/sbin/nologin
""";

        Assert.Null(GreeterUser.Resolve(null, passwd));
    }

    [Fact]
    public void Resolve_AsksWhenSeveralPeopleCouldSignIn()
    {
        Assert.Null(GreeterUser.Resolve(null, TwoUsers));
    }

    [Fact]
    public void Resolve_ConfigBeatsTheAccountList()
    {
        Assert.Equal("alice", GreeterUser.Resolve("user = alice\n", TwoUsers));
    }

    [Fact]
    public void Resolve_AMisspeltConfigDoesNotFallBackToAGuess()
    {
        // Picking the only human account after a typo would sign someone in as
        // the wrong person in a way they could not see; asking is honest.
        Assert.Null(GreeterUser.Resolve("user = michael smith\n", SingleUser));
    }

    [Fact]
    public void FromConfig_IgnoresCommentsBlanksAndOtherKeys()
    {
        const string config = """
# user = root
shell = /bin/bash
""";

        Assert.Null(GreeterUser.FromConfig(config));
    }

    [Fact]
    public void FromConfig_AcceptsSpacingAroundTheKeyAndValue()
    {
        Assert.Equal("michael", GreeterUser.FromConfig("  User   =   michael  \n"));
    }

    [Fact]
    public void FromConfig_OnlyTheFirstUserKeyCounts()
    {
        Assert.Equal("michael", GreeterUser.FromConfig("user = michael\nuser = alice\n"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("michael smith")]
    [InlineData("michael:alice")]
    [InlineData("mich/ael")]
    [InlineData("michael\tlamothe")]
    public void FromConfig_RejectsNamesTheFormCouldNotType(string value)
    {
        Assert.Null(GreeterUser.FromConfig($"user = {value}\n"));
    }

    [Fact]
    public void SingleHumanUser_AcceptsTheTopOfTheUidRange()
    {
        const string passwd = "person:x:1000:1000::/home/person:/bin/zsh\n";

        Assert.Equal("person", GreeterUser.SingleHumanUser(passwd));
    }

    [Fact]
    public void SingleHumanUser_StopsBeforeTheNobodyRange()
    {
        const string passwd = "person:x:59999:59999::/home/person:/bin/bash\nnobody:x:60000:60000::/:/sbin/nologin\n";

        Assert.Equal("person", GreeterUser.SingleHumanUser(passwd));
    }

    [Theory]
    [InlineData("/sbin/nologin")]
    [InlineData("/usr/sbin/nologin")]
    [InlineData("/bin/false")]
    [InlineData("")]
    public void SingleHumanUser_IgnoresAnAccountThatCannotSignIn(string shell)
    {
        var passwd = $"person:x:1000:1000::/home/person:{shell}\n";

        Assert.Null(GreeterUser.SingleHumanUser(passwd));
    }

    [Fact]
    public void Resolve_NoFilesAtAllAsks()
    {
        Assert.Null(GreeterUser.Resolve(null, null));
    }

    [Fact]
    public void State_WithADefaultUser_OpensOnTheSecretField()
    {
        var state = new GreeterState(Sessions(), "michael");
        var snapshot = state.Snapshot();

        Assert.Equal("michael", snapshot.Username);
        Assert.Equal(GreeterField.Secret, snapshot.Focus);
    }

    [Fact]
    public void State_WithADefaultUser_SignsInWithoutTypingTheUsername()
    {
        var state = new GreeterState(Sessions(), "michael");

        // Typing goes straight to the secret, which is the whole point.
        state.Type('1');
        state.Type('2');
        state.Type('3');
        state.Type('4');

        Assert.True(state.TrySubmit(out var username, out var secret, out var answersPrompt));
        Assert.Equal("michael", username);
        Assert.Equal("1234", secret);
        Assert.False(answersPrompt);
    }

    [Fact]
    public void State_WithADefaultUser_StillLetsTheUsernameBeChanged()
    {
        var state = new GreeterState(Sessions(), "michael");

        state.SwitchFocus();
        Assert.Equal(GreeterField.Username, state.Snapshot().Focus);

        state.ClearFocused();
        state.Type('a');
        state.Type('l');
        state.Type('i');
        state.Type('c');
        state.Type('e');

        Assert.True(state.TrySubmit(out var username, out _, out _));
        Assert.Equal("alice", username);
    }

    [Fact]
    public void State_WithoutADefaultUser_StillAsks()
    {
        var state = new GreeterState(Sessions());

        Assert.Equal(string.Empty, state.Snapshot().Username);
        Assert.Equal(GreeterField.Username, state.Snapshot().Focus);
        Assert.False(state.TrySubmit(out _, out _, out _));
    }

    [Fact]
    public void State_RejectsAnInvalidDefaultNameInsteadOfShowingIt()
    {
        var state = new GreeterState(Sessions(), "not a user");

        Assert.Equal(string.Empty, state.Snapshot().Username);
        Assert.Equal(GreeterField.Username, state.Snapshot().Focus);
    }

    private static IReadOnlyList<GreeterSession> Sessions() =>
        [new GreeterSession("rosella.desktop", "Rosella", ["/usr/local/bin/rosella-session"], [])];
}