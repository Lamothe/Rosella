using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Rosella.Compositor.Greeter;
using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// greetd-ipc(7) framing. The protocol is length-prefixed, not
/// line-delimited, and a socket read can end anywhere: inside the prefix,
/// inside a payload, or after several whole messages. Each of those is a test
/// because each one is a way a naive reader loses or corrupts the stream.
/// </summary>
public class GreeterFramingTests
{
    [Fact]
    public void Encode_PrefixesTheUtf8PayloadWithALittleEndianLength()
    {
        const string json = "{\"type\":\"create_session\",\"username\":\"michael\"}";
        var frame = GreeterFraming.Encode(json);
        var payloadLength = Encoding.UTF8.GetByteCount(json);

        Assert.Equal(4 + payloadLength, frame.Length);
        Assert.Equal(payloadLength, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(json, Encoding.UTF8.GetString(frame, 4, payloadLength));
    }

    [Fact]
    public void Encode_MeasuresLengthInBytesNotCharacters()
    {
        // A username or PAM prompt can be non-ASCII; the length is the UTF-8
        // byte count, which is larger than the character count here.
        const string json = "{\"auth_message\":\"Пароль\"}";
        var frame = GreeterFraming.Encode(json);

        Assert.Equal(Encoding.UTF8.GetByteCount(json), BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.True(frame.Length - 4 > json.Length, "the payload should have grown over its character count");
    }

    [Fact]
    public void Read_HandlesALengthPrefixSplitAcrossReads()
    {
        const string json = "{\"type\":\"auth_message\",\"auth_message_type\":\"secret\",\"auth_message\":\"PIN or password\"}";
        var reader = new GreeterFrameReader(new ChunkStream(GreeterFraming.Encode(json), chunk: 1));

        Assert.Equal(json, reader.Read());
    }

    [Fact]
    public void Read_HandlesSeveralMessagesDeliveredInOneRead()
    {
        const string first = "{\"type\":\"auth_message\",\"auth_message_type\":\"secret\",\"auth_message\":\"Password\"}";
        const string second = "{\"type\":\"success\"}";
        var frames = GreeterFraming.Encode(first).Concat(GreeterFraming.Encode(second)).ToArray();
        var stream = new ChunkStream(frames, chunk: frames.Length);
        var reader = new GreeterFrameReader(stream);

        Assert.Equal(first, reader.Read());
        Assert.Equal(second, reader.Read());

        // Both messages were already in the one socket read: the second must
        // come from the reader's buffer, not from a second read.
        Assert.Equal(1, stream.ReadCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(GreeterFraming.MaxMessageBytes + 1)]
    [InlineData(int.MaxValue)]
    public void Read_RejectsABogusLengthBeforeAllocatingThePayload(int length)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, length);
        var reader = new GreeterFrameReader(new ChunkStream(prefix, chunk: 4));

        var exception = Assert.Throws<GreeterProtocolException>(() => reader.Read());
        Assert.Contains("bogus frame length", exception.Message);
    }

    [Fact]
    public void Read_ThrowsEndOfStreamWhenGreetdClosesMidMessage()
    {
        var frame = GreeterFraming.Encode("{\"type\":\"success\"}");
        var reader = new GreeterFrameReader(new ChunkStream(frame[..^3], chunk: 2));

        Assert.Throws<EndOfStreamException>(() => reader.Read());
    }

    [Fact]
    public void Read_RoundTripsNonAsciiJson()
    {
        const string json = "{\"type\":\"auth_message\",\"auth_message_type\":\"visible\",\"auth_message\":\"Код подтверждения\"}";
        var reader = new GreeterFrameReader(new ChunkStream(GreeterFraming.Encode(json), chunk: 3));

        Assert.Equal(json, reader.Read());
    }

    /// <summary>A stream that returns at most <c>chunk</c> bytes per read, counting reads.</summary>
    private sealed class ChunkStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _chunk;
        private int _position;

        internal ChunkStream(byte[] data, int chunk)
        {
            _data = data;
            _chunk = chunk;
        }

        internal int ReadCalls { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _data.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer)
        {
            ReadCalls++;
            var count = Math.Min(Math.Min(_chunk, buffer.Length), _data.Length - _position);
            if (count <= 0)
            {
                return 0;
            }

            _data.AsSpan(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>
/// The greetd conversation as a sequence: success, retryable auth_error,
/// fatal error, and cancel. greetd is explicitly open-ended about how many
/// auth messages arrive, so an info message and a visible prompt are pinned
/// too. Authentication failures never escape this machine as a process exit.
/// </summary>
public class GreeterConversationTests
{
    private static GreeterConversation Started(string username = "michael")
    {
        var conversation = new GreeterConversation();
        conversation.Start(username);
        return conversation;
    }

    [Fact]
    public void Success_AfterSecretResponse_IsReadyToStart()
    {
        var conversation = Started();

        var prompt = conversation.OnMessage(new GreeterAuthMessage("secret", "PIN or password"));
        Assert.Equal(GreeterStepKind.Respond, prompt.Kind);
        Assert.True(prompt.Secret);
        Assert.Equal("PIN or password", prompt.Text);
        Assert.Equal(GreeterPhase.AwaitingResponse, conversation.Phase);

        conversation.Submit();
        Assert.Equal(GreeterPhase.AwaitingResult, conversation.Phase);

        var success = conversation.OnMessage(new GreeterSuccess());
        Assert.Equal(GreeterStepKind.Ready, success.Kind);
        Assert.Equal(GreeterPhase.Ready, conversation.Phase);
        Assert.False(conversation.InFlight);
    }

    [Fact]
    public void Success_WithoutAPrompt_IsReady()
    {
        // A PAM stack can authenticate without asking anything; the session
        // is then ready straight from create_session.
        var conversation = Started();

        var success = conversation.OnMessage(new GreeterSuccess());

        Assert.Equal(GreeterStepKind.Ready, success.Kind);
        Assert.Equal(GreeterPhase.Ready, conversation.Phase);
    }

    [Fact]
    public void AuthError_IsRetryableAndAllowsANewAttempt()
    {
        var conversation = Started();
        conversation.OnMessage(new GreeterAuthMessage("secret", "Password"));
        conversation.Submit();

        var failure = conversation.OnMessage(new GreeterErrorMessage("auth_error", string.Empty));

        Assert.Equal(GreeterStepKind.Retry, failure.Kind);
        Assert.Equal(GreeterConversation.RejectedCredential, failure.Text);
        Assert.Equal(GreeterPhase.Idle, conversation.Phase);

        // Retry is a fresh create_session on the same greeter, not an exit.
        conversation.Start("michael");
        Assert.Equal(GreeterPhase.Created, conversation.Phase);
    }

    [Fact]
    public void AuthError_KeepsTheDescriptionWhenGreetdProvidesOne()
    {
        var conversation = Started();

        var failure = conversation.OnMessage(new GreeterErrorMessage("auth_error", "PIN locked for 5 minutes"));

        Assert.Equal(GreeterStepKind.Retry, failure.Kind);
        Assert.Equal("PIN locked for 5 minutes", failure.Text);
    }

    [Fact]
    public void GeneralError_IsFatalForTheAttemptOnly()
    {
        var conversation = Started();

        var failure = conversation.OnMessage(new GreeterErrorMessage("error", "PAM stack unavailable"));

        Assert.Equal(GreeterStepKind.Fatal, failure.Kind);
        Assert.Equal("PAM stack unavailable", failure.Text);
        Assert.Equal(GreeterPhase.Idle, conversation.Phase);

        // Fatal means this attempt, not the greeter: the next Enter retries.
        conversation.Start("michael");
        Assert.Equal(GreeterPhase.Created, conversation.Phase);
    }

    [Fact]
    public void Cancel_ReturnsToIdleOnlyWhileASessionIsConfigured()
    {
        var conversation = new GreeterConversation();
        Assert.False(conversation.Cancel());

        conversation.Start("michael");
        Assert.True(conversation.InFlight);
        Assert.True(conversation.Cancel());
        Assert.Equal(GreeterPhase.Idle, conversation.Phase);
        Assert.False(conversation.Cancel());
    }

    [Fact]
    public void Cancel_AfterAPromptAlsoCancelsTheConversation()
    {
        var conversation = Started();
        conversation.OnMessage(new GreeterAuthMessage("secret", "Password"));

        Assert.True(conversation.Cancel());
        Assert.Equal(GreeterPhase.Idle, conversation.Phase);

        // The next attempt starts cleanly: no stale prompt or phase.
        conversation.Start("michael");
        Assert.Equal(GreeterPhase.Created, conversation.Phase);
        Assert.Empty(conversation.Prompt);
    }

    [Fact]
    public void InfoMessages_AreAcknowledgedAndKeepTheConversation()
    {
        var conversation = Started();

        var info = conversation.OnMessage(new GreeterAuthMessage("info", "Insert your smartcard"));
        Assert.Equal(GreeterStepKind.Acknowledge, info.Kind);
        Assert.Equal("Insert your smartcard", info.Text);

        // greetd blocks until the response-less acknowledgement arrives.
        conversation.Acknowledge();
        Assert.Equal(GreeterPhase.AwaitingResult, conversation.Phase);

        var secret = conversation.OnMessage(new GreeterAuthMessage("secret", "PIN"));
        Assert.Equal(GreeterStepKind.Respond, secret.Kind);
        Assert.True(secret.Secret);
    }

    [Fact]
    public void InfoMessage_CanBeFollowedBySuccess()
    {
        var conversation = Started();
        Assert.Equal(GreeterStepKind.Acknowledge, conversation.OnMessage(new GreeterAuthMessage("info", "Welcome")).Kind);
        conversation.Acknowledge();

        var success = conversation.OnMessage(new GreeterSuccess());

        Assert.Equal(GreeterStepKind.Ready, success.Kind);
    }

    [Fact]
    public void VisiblePrompt_IsNotSecret()
    {
        var conversation = Started();

        var prompt = conversation.OnMessage(new GreeterAuthMessage("visible", "Verification code"));

        Assert.Equal(GreeterStepKind.Respond, prompt.Kind);
        Assert.False(prompt.Secret);
        Assert.Equal("Verification code", conversation.Prompt);
        Assert.False(conversation.PromptIsSecret);
    }

    [Fact]
    public void UnknownAuthMessageType_IsFatalButNotSticky()
    {
        var conversation = Started();

        var step = conversation.OnMessage(new GreeterAuthMessage("fingerprint", "Touch the reader"));

        Assert.Equal(GreeterStepKind.Fatal, step.Kind);
        Assert.Equal(GreeterPhase.Idle, conversation.Phase);
        conversation.Start("michael");
    }
}

/// <summary>
/// The session picker's source: the same /usr/share/wayland-sessions desktop
/// files GDM reads. Name= is what the picker shows, Exec= is the argv handed
/// to start_session, and hidden entries must not appear.
/// </summary>
public sealed class GreeterSessionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rosella-greeter-{Guid.NewGuid():N}");

    public GreeterSessionTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Load_ReadsNameAndParsesExecIntoArgv()
    {
        Write("gnome.desktop", """
            [Desktop Entry]
            Name=GNOME
            Exec=/usr/bin/gnome-session
            Type=Application
            """);
        Write("rosella.desktop", """
            [Desktop Entry]
            Name=Rosella
            Exec="/opt/Rosella Session/launch" --flag %U
            Type=Application
            """);

        var sessions = GreeterSessions.Load(_root);

        // Sorted by file name, so the picker's order is stable.
        Assert.Equal(["GNOME", "Rosella"], sessions.Select(session => session.Name));
        Assert.Equal(["/usr/bin/gnome-session"], sessions[0].Command);
        Assert.Equal(["/opt/Rosella Session/launch", "--flag"], sessions[1].Command);
    }

    [Fact]
    public void Load_ExcludesHiddenAndUnusableEntries()
    {
        Write("visible.desktop", """
            [Desktop Entry]
            Name=Visible
            Exec=/bin/true
            """);
        Write("nodisplay.desktop", """
            [Desktop Entry]
            Name=No Display
            Exec=/bin/true
            NoDisplay=true
            """);
        Write("hidden.desktop", """
            [Desktop Entry]
            Name=Hidden
            Exec=/bin/true
            Hidden=true
            """);
        Write("noexec.desktop", """
            [Desktop Entry]
            Name=No Exec
            """);
        Write("broken.desktop", """
            [Desktop Entry]
            Name=Broken
            Exec="/unterminated
            """);

        var sessions = GreeterSessions.Load(_root);

        var session = Assert.Single(sessions);
        Assert.Equal("Visible", session.Name);
    }

    [Fact]
    public void Load_FallsBackToTheFileNameWhenNameIsMissing()
    {
        Write("fallback.desktop", """
            [Desktop Entry]
            Exec=/bin/true
            """);

        var session = Assert.Single(GreeterSessions.Load(_root));
        Assert.Equal("fallback", session.Name);
    }

    [Fact]
    public void Load_BuildsTheSessionEnvironmentGreetdDoesNot()
    {
        Write("rosella.desktop", """
            [Desktop Entry]
            Name=Rosella
            Exec=/usr/local/bin/rosella-session
            DesktopNames=Rosella
            """);
        Write("gnome-classic.desktop", """
            [Desktop Entry]
            Name=GNOME Classic
            Exec=gnome-session --session gnome-classic
            DesktopNames=GNOME-Classic;GNOME;
            """);

        var sessions = GreeterSessions.Load(_root);

        // logind reads XDG_SESSION_TYPE before PAM opens the session, so it
        // has to arrive through start_session's env, not the session wrapper.
        Assert.Equal(
            ["XDG_SESSION_TYPE=wayland", "XDG_SESSION_DESKTOP=gnome-classic", "XDG_CURRENT_DESKTOP=GNOME-Classic:GNOME"],
            sessions[0].Environment);
        Assert.Equal(
            ["XDG_SESSION_TYPE=wayland", "XDG_SESSION_DESKTOP=rosella", "XDG_CURRENT_DESKTOP=Rosella"],
            sessions[1].Environment);
    }

    [Fact]
    public void PreferredIndex_PicksRosellaWhenOffered()
    {
        Write("gnome.desktop", "[Desktop Entry]\nName=GNOME\nExec=/bin/true\n");
        Write("rosella.desktop", "[Desktop Entry]\nName=Rosella\nExec=/bin/true\n");

        var sessions = GreeterSessions.Load(_root);

        Assert.Equal(1, GreeterSessions.PreferredIndex(sessions));
        Assert.Equal(0, GreeterSessions.PreferredIndex([]));
    }

    [Fact]
    public void Load_MissingDirectoryIsAnEmptyList()
    {
        Assert.Empty(GreeterSessions.Load(Path.Combine(_root, "does-not-exist")));
    }

    private void Write(string name, string text) => File.WriteAllText(Path.Combine(_root, name), text);
}

/// <summary>
/// The login screen's geometry. It is computed in logical pixels once, and
/// the physical scanout is that layout times ScalePolicy's scale — the same
/// argument the rest of the scene uses. Scale 2 must be twice the pixels and
/// the same card, not a second layout.
/// </summary>
public class GreeterLayoutTests
{
    [Fact]
    public void Compute_CentresTheCardAtScaleOne()
    {
        var layout = GreeterLayout.Compute(1920, 1080, 1);

        Assert.Equal(960, layout.Card.X + (layout.Card.W / 2));
        Assert.Equal(540, layout.Card.Y + (layout.Card.H / 2));
        Assert.InRange(layout.Card.X, 0, 1920);
        Assert.InRange(layout.Card.Y, 0, 1080);
        Assert.True(layout.Card.X + layout.Card.W <= 1920);
        Assert.True(layout.Card.Y + layout.Card.H <= 1080);
    }

    [Fact]
    public void Compute_IsTheSameLogicalLayoutAtScaleTwo()
    {
        var atOne = GreeterLayout.Compute(1920, 1080, 1);
        var atTwo = GreeterLayout.Compute(3840, 2160, 2);

        // The same centred card in logical space: only physical pixels double.
        Assert.Equal(atOne.Card, atTwo.Card);
        Assert.Equal(atOne.UsernameField, atTwo.UsernameField);
        Assert.Equal(atOne.SecretField, atTwo.SecretField);
        Assert.Equal(atOne.SessionRow, atTwo.SessionRow);
        Assert.Equal(atOne.HintLine, atTwo.HintLine);
    }

    [Fact]
    public void Compute_PhysicalGeometryIsExactlyTwiceTheLogicalGeometry()
    {
        var atTwo = GreeterLayout.Compute(3840, 2160, 2);

        Assert.Equal(
            new Rect(atTwo.Card.X * 2, atTwo.Card.Y * 2, atTwo.Card.W * 2, atTwo.Card.H * 2),
            GreeterLayout.Physical(atTwo.Card, 2));
        Assert.Equal(
            new Rect(atTwo.SecretField.X * 2, atTwo.SecretField.Y * 2, atTwo.SecretField.W * 2, atTwo.SecretField.H * 2),
            GreeterLayout.Physical(atTwo.SecretField, 2));

        var card = GreeterLayout.Physical(atTwo.Card, 2);
        Assert.True(card.X + card.W <= 3840);
        Assert.True(card.Y + card.H <= 2160);
    }

    [Fact]
    public void Compute_OnSilversPanel_GivesAnEightyFourPixelPasswordField()
    {
        // 2880x1800 resolves to scale 2, i.e. 1440x900 logical (the value in
        // the live session log). GDM's greeter drew its field at ~8 physical
        // pixels here; this layout is 42 logical, 84 physical.
        var layout = GreeterLayout.Compute(2880, 1800, 2);

        Assert.Equal(720, layout.Card.X + (layout.Card.W / 2));
        Assert.Equal(450, layout.Card.Y + (layout.Card.H / 2));
        Assert.Equal(84, GreeterLayout.Physical(layout.SecretField, 2).H);
        Assert.Equal(42, layout.SecretField.H);
    }

    [Fact]
    public void Compute_StacksEveryRowInsideTheCard()
    {
        var layout = GreeterLayout.Compute(1920, 1080, 1);
        var cardBottom = layout.Card.Y + layout.Card.H;

        Assert.True(layout.Title.Y >= layout.Card.Y);
        Assert.True(layout.UsernameLabel.Y >= layout.Title.Y + layout.Title.H);
        Assert.True(layout.UsernameField.Y >= layout.UsernameLabel.Y + layout.UsernameLabel.H);
        Assert.True(layout.SecretLabel.Y >= layout.UsernameField.Y + layout.UsernameField.H);
        Assert.True(layout.SecretField.Y >= layout.SecretLabel.Y + layout.SecretLabel.H);
        Assert.True(layout.StatusLine.Y >= layout.SecretField.Y + layout.SecretField.H);
        Assert.True(layout.SessionRow.Y >= layout.StatusLine.Y + layout.StatusLine.H);
        Assert.True(layout.HintLine.Y >= layout.SessionRow.Y + layout.SessionRow.H);
        Assert.True(layout.HintLine.Y + layout.HintLine.H <= cardBottom);

        // The inner column is inside the card on both axes.
        Assert.True(layout.UsernameField.X >= layout.Card.X);
        Assert.True(layout.UsernameField.X + layout.UsernameField.W <= layout.Card.X + layout.Card.W);
    }

    [Fact]
    public void Compute_ShortOutputKeepsEveryRowInsideTheCard()
    {
        // The card is never vertically compressed: its border and its rows
        // must agree even when the output is too short to show it whole.
        var layout = GreeterLayout.Compute(800, 360, 2); // 400x180 logical

        Assert.True(layout.HintLine.Y + layout.HintLine.H <= layout.Card.Y + layout.Card.H);
        Assert.True(layout.UsernameField.Y + layout.UsernameField.H <= layout.Card.Y + layout.Card.H);
    }

    [Fact]
    public void Mask_ReplacesEveryCharacterAndCapsLongSecrets()
    {
        Assert.Equal("••••", GreeterScreen.Mask("1234"));
        Assert.Empty(GreeterScreen.Mask(string.Empty));
        Assert.DoesNotContain('x', GreeterScreen.Mask("my secret"));

        // A pasted 128-character secret must not draw outside the field.
        Assert.Equal(32, GreeterScreen.Mask(new string('x', 128)).Length);
    }

    [Fact]
    public void SecretText_MasksSecretPromptsAndEchoesVisibleOnes()
    {
        var secret = new GreeterSnapshot(
            "michael", "1234", "PIN or password", string.Empty, false, true, true, true, GreeterField.Secret, "Rosella", 1);
        Assert.Equal("••••", GreeterScreen.SecretText(secret));

        var visible = secret with { SecretPrompt = false };
        Assert.Equal("1234", GreeterScreen.SecretText(visible));

        Assert.Equal(
            "Response",
            GreeterScreen.SecretText(visible with { Secret = string.Empty, AwaitingInput = true }));
    }

    [Theory]
    [InlineData(1920, 1080, 1)]
    [InlineData(3840, 2160, 2)]
    public void Draw_PaintsTheCardAtScaleOneAndTwo(int width, int height, int scale)
    {
        var state = new GreeterState([new GreeterSession("rosella.desktop", "Rosella", ["/usr/local/bin/rosella-session"], [])]);
        var screen = new GreeterScreen(state);
        var pixels = new byte[width * height * 4];

        screen.Draw(pixels, width, height, width * 4, scale);

        var layout = GreeterLayout.Compute(width, height, scale);
        var centre = PixelAt(pixels, width * 4, (layout.Card.X + (layout.Card.W / 2)) * scale, (layout.Card.Y + (layout.Card.H / 2)) * scale);
        var corner = PixelAt(pixels, width * 4, 0, 0);
        Assert.NotEqual(corner, centre);
    }

    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan((y * stride) + (x * 4)));
}

/// <summary>The form's own rules: validation, field clearing and echo.</summary>
public class GreeterStateTests
{
    private static GreeterState State() =>
        new([new GreeterSession("rosella.desktop", "Rosella", ["/usr/local/bin/rosella-session"], [])]);

    [Fact]
    public void Submit_WithoutAUsernameShowsAnErrorAndDoesNotStart()
    {
        var state = State();

        Assert.False(state.TrySubmit(out _, out _, out _));
        Assert.Contains("username", state.Snapshot().Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(state.Snapshot().Busy);
    }

    [Fact]
    public void SelectedSession_DefaultsToRosella()
    {
        var state = new GreeterState([
            new GreeterSession("gnome.desktop", "GNOME", ["/bin/true"], []),
            new GreeterSession("rosella.desktop", "Rosella", ["/bin/true"], []),
        ]);

        Assert.Equal("Rosella", state.Snapshot().SessionName);
    }

    [Fact]
    public void Prompt_EchoFollowsTheAuthMessageType()
    {
        var state = State();

        state.SetPrompt("PIN or password", secretPrompt: true);
        Assert.True(state.Snapshot().SecretPrompt);
        Assert.True(state.Snapshot().AwaitingInput);

        state.ResponseSent();
        state.SetPrompt("Verification code", secretPrompt: false);
        Assert.False(state.Snapshot().SecretPrompt);
    }

    [Fact]
    public void Submit_FlagsAnAnswerToAnOpenPrompt()
    {
        var state = State();
        state.SetPrompt("PIN or password", secretPrompt: true);
        state.Type('1');

        Assert.True(state.TrySubmit(out _, out var secret, out var answersPrompt));
        Assert.Equal("1", secret);
        Assert.True(answersPrompt);
    }

    [Fact]
    public void AuthFailure_ClearsTheSecretAndKeepsTheUsername()
    {
        var state = State();
        foreach (var character in "michael")
        {
            state.Type(character);
        }

        state.SwitchFocus();
        foreach (var character in "1234")
        {
            state.Type(character);
        }

        Assert.True(state.TrySubmit(out var username, out var secret, out var answersPrompt));
        Assert.False(answersPrompt);
        Assert.Equal("michael", username);
        Assert.Equal("1234", secret);
        Assert.Empty(state.Snapshot().Secret);

        state.Fail(GreeterConversation.RejectedCredential);

        var snapshot = state.Snapshot();
        Assert.Equal("michael", snapshot.Username);
        Assert.Empty(snapshot.Secret);
        Assert.False(snapshot.Busy);
        Assert.True(snapshot.MessageIsError);
    }
}

/// <summary>The US layout Rosella's keymap already uses, now feeding the login form.</summary>
public class GreeterKeyTests
{
    [Fact]
    public void Map_TranslatesLettersDigitsAndEditingKeys()
    {
        Assert.Equal(new GreeterKey(GreeterKeyAction.Character, 'a'), GreeterKeys.Map(30, shift: false));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Character, 'A'), GreeterKeys.Map(30, shift: true));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Character, '!'), GreeterKeys.Map(2, shift: true));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Character, '7'), GreeterKeys.Map(8, shift: false));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Enter), GreeterKeys.Map(28, shift: false));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Enter), GreeterKeys.Map(96, shift: false));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Escape), GreeterKeys.Map(1, shift: false));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Backspace), GreeterKeys.Map(14, shift: false));
        Assert.Equal(new GreeterKey(GreeterKeyAction.Tab), GreeterKeys.Map(15, shift: false));
        Assert.Equal(GreeterKeyAction.None, GreeterKeys.Map(125, shift: false).Action);
    }
}
/// <summary>
/// The controller is the only place where the pure conversation, the form and
/// the socket meet. These tests script greetd's side of the stream, so the
/// JSON actually written — create_session, the answer, start_session,
/// cancel_session — is asserted end to end, without a real greetd.
/// </summary>
public class GreeterControllerTests
{
    [Fact]
    public void Success_SendsCreateSessionTheAnswerAndStartSession()
    {
        var state = State();
        var server = new ScriptedDuplex(
            Prompt("secret", "PIN or password"),
            "{\"type\":\"success\"}",
            "{\"type\":\"success\"}"); // the start_session reply
        using var quit = new ManualResetEventSlim();
        using var controller = new GreeterController(state, server, transport: null, quit.Set);

        controller.Start();
        SignIn(controller, "michael", "1234");

        Assert.True(quit.Wait(TimeSpan.FromSeconds(5)), "the greeter did not hand the session over");
        Assert.Equal(0, controller.ExitCode);

        var requests = server.Requests();
        Assert.Equal(3, requests.Length);
        Assert.Equal("create_session", StringField(requests[0], "type"));
        Assert.Equal("michael", StringField(requests[0], "username"));
        Assert.Equal("post_auth_message_response", StringField(requests[1], "type"));
        Assert.Equal("1234", StringField(requests[1], "response"));
        Assert.Equal("start_session", StringField(requests[2], "type"));
        Assert.Equal(["/usr/local/bin/rosella-session"], StringArrayField(requests[2], "cmd"));
        Assert.Equal(
            ["XDG_SESSION_TYPE=wayland", "XDG_SESSION_DESKTOP=rosella", "XDG_CURRENT_DESKTOP=Rosella"],
            StringArrayField(requests[2], "env"));
    }

    [Fact]
    public void InfoMessage_IsAcknowledgedWithoutAResponseField()
    {
        var state = State();
        var server = new ScriptedDuplex(
            Prompt("info", "PIN lockout expires in 5 minutes"),
            Prompt("secret", "PIN or password"),
            "{\"type\":\"success\"}",
            "{\"type\":\"success\"}");
        using var quit = new ManualResetEventSlim();
        using var controller = new GreeterController(state, server, transport: null, quit.Set);

        controller.Start();
        SignIn(controller, "michael", "1234");

        Assert.True(quit.Wait(TimeSpan.FromSeconds(5)));

        var requests = server.Requests();
        Assert.Equal(4, requests.Length);
        Assert.Equal("create_session", StringField(requests[0], "type"));
        Assert.Equal("post_auth_message_response", StringField(requests[1], "type"));

        // greetd rejects Some("") for an informative message: the field must
        // be absent, not empty.
        Assert.False(HasField(requests[1], "response"));
        Assert.Equal("post_auth_message_response", StringField(requests[2], "type"));
        Assert.Equal("1234", StringField(requests[2], "response"));
        Assert.Equal("start_session", StringField(requests[3], "type"));
    }

    [Fact]
    public void AuthError_CancelsTheConfiguredSessionBeforeTheRetry()
    {
        var state = State();
        var server = new ScriptedDuplex(
            Prompt("secret", "PIN or password"),
            "{\"type\":\"error\",\"error_type\":\"auth_error\",\"description\":\"\"}",
            "{\"type\":\"success\"}", // the cancel_session reply
            Prompt("secret", "PIN or password"),
            "{\"type\":\"success\"}",
            "{\"type\":\"success\"}"); // the start_session reply
        using var quit = new ManualResetEventSlim();
        using var controller = new GreeterController(state, server, transport: null, quit.Set);

        controller.Start();
        SignIn(controller, "michael", "0000");
        WaitUntil(() => state.Snapshot().Message == GreeterConversation.RejectedCredential);

        // The username survives the rejection; the secret does not; focus is
        // already on the secret field so the retry is just type and Enter.
        Assert.False(state.Snapshot().Busy);
        Assert.Equal("michael", state.Snapshot().Username);
        Assert.Empty(state.Snapshot().Secret);
        Type(controller, "1234");
        controller.Key(InputConstants.KeyEnter, InputConstants.KeyPressed);

        Assert.True(quit.Wait(TimeSpan.FromSeconds(5)), "the retry did not hand the session over");

        var requests = server.Requests();
        Assert.Equal(6, requests.Length);
        Assert.Equal("cancel_session", StringField(requests[2], "type"));
        Assert.Equal(2, requests.Count(request => StringField(request, "type") == "create_session"));
        Assert.Equal("create_session", StringField(requests[3], "type"));
        Assert.Equal("1234", StringField(requests[4], "response"));
        Assert.Equal("start_session", StringField(requests[5], "type"));
    }

    [Fact]
    public void Escape_OnAnOpenPromptSendsCancelSessionAndReopensTheForm()
    {
        var state = State();
        var server = new ScriptedDuplex(
            Prompt("secret", "PIN or password"),
            Prompt("visible", "Verification code"),
            "{\"type\":\"success\"}"); // the cancel_session reply
        using var controller = new GreeterController(state, server, transport: null, () => { });

        controller.Start();
        SignIn(controller, "michael", "1234");
        WaitUntil(() => state.Snapshot().AwaitingInput);
        controller.Key(InputConstants.KeyEsc, InputConstants.KeyPressed);
        WaitUntil(() => state.Snapshot().Message == "Sign-in cancelled.");

        var requests = server.Requests();
        Assert.Equal(3, requests.Length);
        Assert.Equal("create_session", StringField(requests[0], "type"));
        Assert.Equal("post_auth_message_response", StringField(requests[1], "type"));
        Assert.Equal("cancel_session", StringField(requests[2], "type"));
        Assert.False(state.Snapshot().Busy);
    }

    [Fact]
    public void Cancel_ThenANewSignInIsAlignedAndStarts()
    {
        var state = State();
        var server = new ScriptedDuplex(
            Prompt("secret", "PIN or password"),
            Prompt("visible", "Verification code"),
            "{\"type\":\"success\"}", // the cancel_session reply
            Prompt("secret", "PIN or password"),
            "{\"type\":\"success\"}",
            "{\"type\":\"success\"}"); // the start_session reply
        using var quit = new ManualResetEventSlim();
        using var controller = new GreeterController(state, server, transport: null, quit.Set);

        controller.Start();
        SignIn(controller, "michael", "1234");
        WaitUntil(() => state.Snapshot().AwaitingInput);
        controller.Key(InputConstants.KeyEsc, InputConstants.KeyPressed);
        WaitUntil(() => state.Snapshot().Message == "Sign-in cancelled.");

        // The username survived the cancel, focus is the secret field.
        Type(controller, "5678");
        controller.Key(InputConstants.KeyEnter, InputConstants.KeyPressed);
        Assert.True(quit.Wait(TimeSpan.FromSeconds(5)), "the post-cancel sign-in did not hand the session over");

        var requests = server.Requests();
        Assert.Equal(6, requests.Length);
        Assert.Equal("cancel_session", StringField(requests[2], "type"));
        Assert.Equal("create_session", StringField(requests[3], "type"));
        Assert.Equal("5678", StringField(requests[4], "response"));
        Assert.Equal("start_session", StringField(requests[5], "type"));
    }

    [Fact]
    public void StartSessionError_ReopensTheFormAndTheRetrySucceeds()
    {
        var state = State();
        var server = new ScriptedDuplex(
            Prompt("secret", "PIN or password"),
            "{\"type\":\"success\"}",
            "{\"type\":\"error\",\"error_type\":\"error\",\"description\":\"session is not ready\"}",
            "{\"type\":\"success\"}", // the cancel_session reply
            Prompt("secret", "PIN or password"),
            "{\"type\":\"success\"}",
            "{\"type\":\"success\"}"); // the second start_session reply
        using var quit = new ManualResetEventSlim();
        using var controller = new GreeterController(state, server, transport: null, quit.Set);

        controller.Start();
        SignIn(controller, "michael", "1234");

        // Wait for the cancel's reply to have been consumed, not just for the
        // error line: the retry below depends on the stream being aligned.
        WaitUntil(() => server.Requests().Length == 4);
        Assert.False(quit.Wait(TimeSpan.FromMilliseconds(100)), "a failed start_session must not end the greeter");
        Assert.False(state.Snapshot().Busy);
        Assert.Contains("session is not ready", state.Snapshot().Message, StringComparison.Ordinal);

        // Ready leaked before this fix; the next Attempt then threw on the
        // driver thread and killed the greeter. It must simply start over.
        Type(controller, "5678");
        controller.Key(InputConstants.KeyEnter, InputConstants.KeyPressed);
        Assert.True(quit.Wait(TimeSpan.FromSeconds(5)), "the retry after a failed start_session did not start");

        var requests = server.Requests();
        Assert.Equal(7, requests.Length);
        Assert.Equal("start_session", StringField(requests[2], "type"));
        Assert.Equal("cancel_session", StringField(requests[3], "type"));
        Assert.Equal("create_session", StringField(requests[4], "type"));
        Assert.Equal("5678", StringField(requests[5], "response"));
        Assert.Equal("start_session", StringField(requests[6], "type"));
    }

    [Fact]
    public void Dispose_WithoutStartDoesNotThrow()
    {
        var state = State();
        using var controller = new GreeterController(state, new ScriptedDuplex(), transport: null, () => { });

        controller.Dispose();
    }

    private static GreeterState State() =>
        new([
            new GreeterSession(
                "rosella.desktop",
                "Rosella",
                ["/usr/local/bin/rosella-session"],
                ["XDG_SESSION_TYPE=wayland", "XDG_SESSION_DESKTOP=rosella", "XDG_CURRENT_DESKTOP=Rosella"]),
        ]);

    private static string Prompt(string type, string text) =>
        $"{{\"type\":\"auth_message\",\"auth_message_type\":\"{type}\",\"auth_message\":\"{text}\"}}";

    private static void SignIn(GreeterController controller, string username, string secret)
    {
        Type(controller, username);
        controller.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        Type(controller, secret);
        controller.Key(InputConstants.KeyEnter, InputConstants.KeyPressed);
    }

    private static void Type(GreeterController controller, string text)
    {
        foreach (var character in text)
        {
            controller.Key(KeyCode(character), InputConstants.KeyPressed);
        }
    }

    private static ushort KeyCode(char character) => character switch
    {
        'a' => 30,
        'b' => 48,
        'c' => 46,
        'd' => 32,
        'e' => 18,
        'f' => 33,
        'g' => 34,
        'h' => 35,
        'i' => 23,
        'j' => 36,
        'k' => 37,
        'l' => 38,
        'm' => 50,
        'n' => 49,
        'o' => 24,
        'p' => 25,
        'q' => 16,
        'r' => 19,
        's' => 31,
        't' => 20,
        'u' => 22,
        'v' => 47,
        'w' => 17,
        'x' => 45,
        'y' => 21,
        'z' => 44,
        '0' => 11,
        '1' => 2,
        '2' => 3,
        '3' => 4,
        '4' => 5,
        '5' => 6,
        '6' => 7,
        '7' => 8,
        '8' => 9,
        '9' => 10,
        _ => throw new ArgumentOutOfRangeException(nameof(character), character, "test key map is missing a character"),
    };

    private static void WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = Environment.TickCount64 + milliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("the greeter did not reach the expected state");
            }

            Thread.Sleep(5);
        }
    }

    private static string? StringField(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool HasField(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out _);
    }

    private static string[] StringArrayField(string json, string name)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(item => item.GetString() ?? string.Empty)]
            : [];
    }

    /// <summary>
    /// greetd's side of the stream: scripted messages to read, captured frames
    /// to inspect. It is a Stream so the controller's real framing runs.
    /// </summary>
    private sealed class ScriptedDuplex : Stream
    {
        private readonly MemoryStream _inbound = new();
        private readonly MemoryStream _outbound = new();
        private readonly object _outboundGate = new();

        internal ScriptedDuplex(params string[] serverMessages)
        {
            foreach (var message in serverMessages)
            {
                var frame = GreeterFraming.Encode(message);
                _inbound.Write(frame, 0, frame.Length);
            }

            _inbound.Position = 0;
        }

        internal string[] Requests()
        {
            lock (_outboundGate)
            {
                var reader = new GreeterFrameReader(new MemoryStream(_outbound.ToArray()));
                var requests = new List<string>();
                try
                {
                    while (true)
                    {
                        requests.Add(reader.Read());
                    }
                }
                catch (EndOfStreamException)
                {
                    // All complete frames were captured.
                }

                return [.. requests];
            }
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(Span<byte> buffer) => _inbound.Read(buffer);

        public override int Read(byte[] buffer, int offset, int count) => _inbound.Read(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            lock (_outboundGate)
            {
                _outbound.Write(buffer);
            }
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            lock (_outboundGate)
            {
                _outbound.Write(buffer, offset, count);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
